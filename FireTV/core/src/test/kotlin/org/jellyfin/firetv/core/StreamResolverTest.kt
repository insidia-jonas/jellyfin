package org.jellyfin.firetv.core

import com.sun.net.httpserver.HttpServer
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.util.concurrent.Executors

class StreamResolverTest {
    @Test
    fun `explicit IPTV selection cannot silently use the default source`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/Items/channel/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val body = """{"MediaSources":[{"Id":"default","IsInfiniteStream":true,"SupportsDirectStream":true,"DirectStreamUrl":"/LiveTv/LiveStreamFiles/default/stream.ts"}]}""".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.start()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            org.junit.jupiter.api.Assertions.assertThrows(IllegalArgumentException::class.java) {
                StreamResolver.resolve("""{"serverAddress":"$base","items":[{"Id":"channel","Type":"TvChannel"}],"mediaSourceId":"default_iptv_other"}""", false)
            }
        } finally { server.stop(0) }
    }

    @Test
    fun `finite episode opens dynamic source without live TV controls or losing resume`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var opens = 0
        server.createContext("/Items/episode/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val body = """{"MediaSources":[{"Id":"vod","RequiresOpening":true,"OpenToken":"dynamic-vod"}]}""".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.createContext("/LiveStreams/Open") { exchange ->
            exchange.requestBody.readBytes(); opens++
            val body = """{"MediaSource":{"Id":"vod","LiveStreamId":"finite-handle","SupportsDirectStream":true,"DirectStreamUrl":"/Videos/episode/stream.mkv"}}""".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.start()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val result = StreamResolver.resolve("""{"serverAddress":"$base","items":[{"Id":"episode","Type":"Episode","SeriesName":"Example","IndexNumber":3,"ParentIndexNumber":1}],"startPositionTicks":1230000000}""", false)
            assertEquals(false, result.isLive)
            assertEquals(123000L, result.startPositionMs)
            assertEquals("finite-handle", result.liveStreamId)
            assertEquals(1, opens)
            assertTrue(result.url.startsWith("$base/Videos/episode/stream.mkv"))
        } finally { server.stop(0) }
    }

    @Test
    fun `local direct play without stream URLs uses authenticated static media endpoint`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/Items/local-file/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val body = """{"PlaySessionId":"session","MediaSources":[{"Id":"version+1","Protocol":"File","Path":"/media/film.mkv","Container":"mkv","SupportsDirectPlay":true,"SupportsDirectStream":true}]}""".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.start()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val video = StreamResolver.resolve("""{"ids":["local-file"],"serverAddress":"$base","accessToken":"private","userId":"u","startPositionTicks":100000000}""", false)
            assertTrue(video.url.startsWith("$base/Videos/local-file/stream?static=true&MediaSourceId=version%2B1"))
            assertTrue(video.url.contains("api_key=private"))
            assertTrue(video.url.contains("PlaySessionId=session"))
            assertEquals("DirectPlay", video.playMethod)
            assertEquals(10000L, video.startPositionMs)
            val audio = StreamResolver.resolve("""{"items":[{"Id":"local-file","MediaType":"Audio"}],"serverAddress":"$base","accessToken":"private","userId":"u"}""", false)
            assertTrue(audio.url.startsWith("$base/Audio/local-file/stream?static=true"))
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `unsupported local files and remote sources do not acquire static direct play URLs`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var source = """{"Id":"x","Protocol":"File","Path":"/media/file.mkv","SupportsDirectPlay":false}"""
        server.createContext("/Items/x/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val body = """{"MediaSources":[$source]}""".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.start()
        try {
            val payload = """{"ids":["x"],"serverAddress":"http://127.0.0.1:${server.address.port}","userId":"u"}"""
            org.junit.jupiter.api.Assertions.assertThrows(IllegalStateException::class.java) { StreamResolver.resolve(payload, false) }
            source = """{"Id":"x","Protocol":"Http","SupportsDirectPlay":true}"""
            org.junit.jupiter.api.Assertions.assertThrows(IllegalStateException::class.java) { StreamResolver.resolve(payload, false) }
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `resolves direct stream url from a local jellyfin-compatible server`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var capturedBody = ""
        var capturedLanguage = ""
        server.createContext("/Items/movie-1/PlaybackInfo") { exchange ->
            capturedLanguage = exchange.requestHeaders.getFirst("Accept-Language").orEmpty()
            capturedBody = exchange.requestBody.readBytes().toString(Charsets.UTF_8)
            val json = """
                {
                  "PlaySessionId": "session-1",
                  "MediaSources": [{
                    "Id": "source-1",
                    "Path": "/mnt/media/Arrival.mkv",
                    "SupportsDirectPlay": true,
                    "SupportsDirectStream": true,
                    "DirectStreamUrl": "/Videos/movie-1/stream.mkv?static=true",
                    "TranscodingUrl": "/videos/movie-1/master.m3u8",
                    "DefaultAudioStreamIndex": 1,
                    "MediaStreams": [
                      {"Index":0,"Type":"Video","Codec":"hevc"},
                      {"Index":1,"Type":"Audio","Language":"eng","DisplayTitle":"English","IsDefault":true},
                      {"Index":2,"Type":"Subtitle","Language":"ger","DisplayTitle":"German","DeliveryMethod":"External","DeliveryUrl":"/Videos/movie-1/source-1/Subtitles/2/Stream.vtt"}
                    ]
                  }]
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.executor = Executors.newSingleThreadExecutor()
        server.start()
        try {
            val port = server.address.port
            val payload = """
                {
                  "items":[{"Id":"movie-1","Name":"Arrival"}],
                  "serverAddress":"http://127.0.0.1:$port",
                  "accessToken":"secret-token",
                  "userId":"user-1",
                  "deviceId":"firetv-test",
                  "startPositionTicks": 100000000
                }
            """.trimIndent()
            val resolved = StreamResolver.resolve(payload, ignoreSslErrors = false)
            assertEquals("Arrival", resolved.title)
            assertEquals("movie-1", resolved.itemId)
            assertEquals("source-1", resolved.mediaSourceId)
            assertEquals("session-1", resolved.playSessionId)
            assertEquals(10_000L, resolved.startPositionMs)
            assertTrue(resolved.url.startsWith("http://127.0.0.1:$port/Videos/movie-1/stream.mkv"))
            assertTrue(resolved.url.contains("api_key=secret-token"))
            assertTrue(capturedBody.contains("\"EnableDirectPlay\":true"))
            assertTrue(capturedBody.contains("DeviceProfile"))
            assertTrue(capturedBody.contains("VideoRotation"))
            assertTrue(capturedBody.contains("vobsub"))
            assertTrue(capturedBody.contains("\"Container\":\"mp4\""))
            assertTrue(capturedLanguage.isNotBlank())
            assertEquals(1, resolved.audioTracks.size)
            assertEquals(1, resolved.subtitleTracks.size)
            assertEquals("German", resolved.subtitleTracks[0].displayTitle)
            assertEquals(1, resolved.selectedAudioIndex)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `falls back to hls when only a filesystem path is present`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/Items/movie-2/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val json = """
                {
                  "PlaySessionId": "session-2",
                  "MediaSources": [{
                    "Id": "source-2",
                    "Path": "/data/films/x.mkv",
                    "SupportsDirectPlay": true,
                    "SupportsDirectStream": false,
                    "TranscodingUrl": "/videos/movie-2/master.m3u8"
                  }]
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.executor = Executors.newSingleThreadExecutor()
        server.start()
        try {
            val port = server.address.port
            val payload = """
                {
                  "ids":["movie-2"],
                  "serverAddress":"http://127.0.0.1:$port",
                  "accessToken":"t",
                  "userId":"u"
                }
            """.trimIndent()
            val resolved = StreamResolver.resolve(payload, ignoreSslErrors = false)
            assertTrue(resolved.url.contains("/videos/movie-2/master.m3u8"))
            assertEquals("Transcode", resolved.playMethod)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `opens a live m3u channel when playback info still requires opening`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var opened = false
        server.createContext("/Items/channel-1/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val json = """
                {
                  "PlaySessionId": "live-session",
                  "MediaSources": [{
                    "Id": "live-source",
                    "Path": "http://ingest.example/stream.ts",
                    "RequiresOpening": true,
                    "IsInfiniteStream": true,
                    "SupportsDirectPlay": true,
                    "OpenToken": "token-1"
                  }]
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.createContext("/LiveStreams/Open") { exchange ->
            opened = true
            exchange.requestBody.readBytes()
            val json = """
                {
                  "MediaSource": {
                    "Id": "live-source",
                    "Path": "http://127.0.0.1/LiveStreams/abc/stream.ts",
                    "LiveStreamId": "abc",
                    "IsInfiniteStream": true,
                    "RequiresOpening": false,
                    "SupportsDirectPlay": true,
                    "Container": "mpegts"
                  }
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.executor = Executors.newSingleThreadExecutor()
        server.start()
        try {
            val port = server.address.port
            val payload = """
                {
                  "items":[{"Id":"channel-1","Name":"Das Erste","Type":"TvChannel"}],
                  "serverAddress":"http://127.0.0.1:$port",
                  "accessToken":"t",
                  "userId":"u"
                }
            """.trimIndent()
            val resolved = StreamResolver.resolve(payload, ignoreSslErrors = false)
            assertTrue(opened)
            assertTrue(resolved.isLive)
            assertEquals("abc", resolved.liveStreamId)
            assertEquals("mpegts", resolved.container)
            assertTrue(resolved.url.contains("/LiveStreams/abc/stream.ts"))
            assertTrue(resolved.url.contains("api_key=t"))
            assertTrue(resolved.url.startsWith("http://127.0.0.1:$port/"))
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `skips an empty file source and rewrites the loopback live proxy`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var openedToken = ""
        server.createContext("/Items/m3u_zdf/PlaybackInfo") { exchange ->
            exchange.requestBody.readBytes()
            val json = """
                {
                  "PlaySessionId": "live-session-2",
                  "MediaSources": [
                    {"Id":"file","Protocol":"File","Path":"","SupportsDirectPlay":true},
                    {"Id":"tuner","RequiresOpening":true,"IsInfiniteStream":true,"OpenToken":"provider_m3u_zdf|tuner_iptv_choice","Path":"http://nl01.provider.example/zdf.ts","SupportsDirectPlay":true}
                  ]
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.createContext("/LiveStreams/Open") { exchange ->
            openedToken = exchange.requestBody.readBytes().toString(Charsets.UTF_8)
            val json = """
                {
                  "MediaSource": {
                    "Id": "tuner",
                    "Path": "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/zdf/stream.ts",
                    "LiveStreamId": "zdf",
                    "IsInfiniteStream": true,
                    "Container": "mpegts"
                  }
                }
            """.trimIndent()
            val bytes = json.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.executor = Executors.newSingleThreadExecutor()
        server.start()
        try {
            val port = server.address.port
            val payload = """
                {
                  "items":[{"Id":"m3u_zdf","Name":"ZDF","Type":"TvChannel","ExternalId":"m3u_zdf","IsLiveStream":true}],
                  "serverAddress":"http://127.0.0.1:$port",
                  "accessToken":"t",
                  "userId":"u"
                }
            """.trimIndent()
            val resolved = StreamResolver.resolve(payload, ignoreSslErrors = false)
            assertEquals("provider_m3u_zdf|tuner_iptv_choice", jsonStringField(openedToken, "OpenToken"))
            assertTrue(resolved.isLive)
            assertEquals("http://127.0.0.1:$port/LiveTv/LiveStreamFiles/zdf/stream.ts?api_key=t", resolved.url)
            assertTrue(!resolved.url.contains("provider.example"))
        } finally {
            server.stop(0)
        }
    }
}
