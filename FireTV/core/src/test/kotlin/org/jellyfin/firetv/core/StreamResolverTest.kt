package org.jellyfin.firetv.core

import com.sun.net.httpserver.HttpServer
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress
import java.util.concurrent.Executors

class StreamResolverTest {
    @Test
    fun `resolves direct stream url from a local jellyfin-compatible server`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var capturedBody = ""
        server.createContext("/Items/movie-1/PlaybackInfo") { exchange ->
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
                    "TranscodingUrl": "/videos/movie-1/master.m3u8"
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
}
