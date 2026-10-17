package org.jellyfin.firetv.core

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class LiveVideoCompatibilityTest {
    @Test
    fun `workaround is restricted to the affected Cube generation`() {
        assertTrue(DeviceProfile.requiresProgressiveLiveVideo("AFTR", 28))
        assertFalse(DeviceProfile.requiresProgressiveLiveVideo("AFTR", 30))
        assertFalse(DeviceProfile.requiresProgressiveLiveVideo("AFTGAZL", 28))
    }

    @Test
    fun `opened interlaced source uses HLS while progressive live and films remain direct`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var interlaced = true
        var transcode = true
        val requests = mutableListOf<String>()
        server.createContext("/") { exchange ->
            requests.add(exchange.requestBody.readBytes().toString(Charsets.UTF_8))
            val source = """{"Id":"source","Protocol":"Http","Path":"/LiveTv/LiveStreamFiles/live/stream.ts","SupportsDirectPlay":true,"SupportsDirectStream":true,"DirectStreamUrl":"/Videos/channel/stream.mkv","LiveStreamId":"live","IsInfiniteStream":true,"MediaStreams":[{"Type":"Video","Codec":"h264","IsInterlaced":$interlaced,"AverageFrameRate":25}]${if (transcode) ",\"TranscodingUrl\":\"/Videos/channel/master.m3u8\"" else ""}}"""
            val response = if (exchange.requestURI.path == "/LiveStreams/Open") """{"MediaSource":$source}"""
                else """{"PlaySessionId":"session","MediaSources":[{"Id":"source","RequiresOpening":true,"OpenToken":"open"}]}"""
            val bytes = response.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val payload = """{"serverAddress":"$base","userId":"u","items":[{"Id":"channel","Type":"TvChannel"}]}"""
            val resolved = StreamResolver.resolve(payload, false, progressiveLiveVideo = true)
            assertEquals("Transcode", resolved.playMethod)
            assertTrue(resolved.url.contains("/Videos/channel/master.m3u8"))
            assertEquals("live", resolved.liveStreamId)
            assertEquals(2, requests.size)
            for (request in requests) {
                val profile = jsonObjectField(request, "DeviceProfile")!!
                assertTrue(profile.contains("IsInterlaced"))
                assertTrue(jsonArrayObjects(profile, "TranscodingProfiles").all { jsonStringField(it, "Protocol") == "hls" })
                assertTrue(jsonArrayObjects(profile, "TranscodingProfiles").all { jsonStringField(it, "MinSegments") == "3" })
                assertEquals(120000000L, jsonLongField(request, "MaxStreamingBitrate"))
            }
            interlaced = false
            assertTrue(StreamResolver.resolve(payload, false, progressiveLiveVideo = true).url.contains("/LiveTv/LiveStreamFiles/"))
            interlaced = true
            transcode = false
            assertThrows(IllegalStateException::class.java) { StreamResolver.resolve(payload, false, progressiveLiveVideo = true) }
            requests.clear()
            StreamResolver.resolve(payload.replace("TvChannel", "Episode"), false, progressiveLiveVideo = true)
            assertFalse(jsonObjectField(requests.first(), "DeviceProfile")!!.contains("IsInterlaced"))
        } finally { server.stop(0) }
    }
}
