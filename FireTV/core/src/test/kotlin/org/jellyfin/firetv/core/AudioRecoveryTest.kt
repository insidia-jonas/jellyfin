package org.jellyfin.firetv.core

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class AudioRecoveryTest {
    @Test
    fun `audio recovery is bounded across track changes but resets for another title`() {
        val recovery = AudioRecovery()
        recovery.select("episode", true)
        assertFalse(recovery.recover(2002, true, false)) // Network timeout, not codec failure.
        assertFalse(recovery.recover(4003, false, false)) // Video decoder.
        assertFalse(recovery.recover(5001, true, true)) // Live TV owns its retry budget.
        assertTrue(recovery.recover(4001, true, false))
        recovery.select("episode", false)
        assertTrue(recovery.enabled)
        assertFalse(recovery.recover(5002, true, false))
        recovery.select("next-episode", false)
        assertFalse(recovery.enabled)
        assertTrue(recovery.recover(5001, false, false)) // Sink failures need no format.
        recovery.select("next-episode", true)
        assertFalse(recovery.enabled)
    }

    @Test
    fun `compatibility requests converted stereo audio with the same source tracks and position`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var request = ""
        var offerTranscode = true
        server.createContext("/Items/episode/PlaybackInfo") { exchange ->
            request = exchange.requestBody.readBytes().toString(Charsets.UTF_8)
            val url = if (offerTranscode) "\"TranscodingUrl\":\"/Videos/episode/master.m3u8\"," else ""
            // Even if an old server advertises passthrough, don't select it again.
            val bytes = """{"MediaSources":[{${url}"Id":"version","Protocol":"File","Path":"/media/episode.mkv","SupportsDirectPlay":true,"SupportsDirectStream":true,"DirectStreamUrl":"/Videos/episode/stream.mkv","DefaultAudioStreamIndex":1,"MediaStreams":[{"Index":2,"Type":"Audio","Language":"deu","Codec":"eac3"}]}]}""".toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val payload = """{"serverAddress":"$base","items":[{"Id":"episode","Type":"Episode"}],"userId":"u","accessToken":"test","mediaSourceId":"version","startPositionTicks":1234500000}"""
            val result = StreamResolver.resolve(payload, false, 2, -1, audioCompatibility = true)
            assertTrue(result.url.startsWith("$base/Videos/episode/master.m3u8"))
            assertEquals("Transcode", result.playMethod)
            assertEquals(123450L, result.startPositionMs)
            assertEquals(2, result.selectedAudioIndex)
            assertEquals(-1, result.selectedSubtitleIndex)
            assertFalse(result.isLive)
            assertEquals(false, jsonBooleanField(request, "EnableDirectPlay"))
            assertEquals(false, jsonBooleanField(request, "EnableDirectStream"))
            assertEquals(false, jsonBooleanField(request, "AllowAudioStreamCopy"))
            assertEquals(true, jsonBooleanField(request, "AllowVideoStreamCopy"))
            assertEquals("version", jsonStringField(request, "MediaSourceId"))
            assertEquals(1234500000L, jsonLongField(request, "StartTimeTicks"))
            assertEquals(2L, jsonLongField(request, "AudioStreamIndex"))
            val profile = jsonObjectField(request, "DeviceProfile")!!
            val video = jsonArrayObjects(profile, "TranscodingProfiles").first()
            assertEquals("aac", jsonStringField(video, "AudioCodec"))
            assertEquals("2", jsonStringField(video, "MaxAudioChannels"))
            offerTranscode = false
            assertThrows(IllegalStateException::class.java) {
                StreamResolver.resolve(payload, false, audioCompatibility = true)
            }
        } finally { server.stop(0) }
    }
}
