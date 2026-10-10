package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class LivePlaybackTest {
    @Test
    fun `library types override stale flags and dynamic stream handles`() {
        for (type in listOf("Movie", "Episode", "Audio")) {
            val payload = """{"IsLiveStream":true,"items":[{"Id":"film","Type":"$type","IsLiveStream":true}]}"""
            assertFalse(LivePlayback.isLivePayload(payload))
            assertFalse(LivePlayback.isLive(payload, """{"RequiresOpening":true,"LiveStreamId":"handle","IsInfiniteStream":true}"""))
        }
        assertFalse(LivePlayback.isLiveSource("""{"RequiresOpening":true,"LiveStreamId":"dynamic-vod"}"""))
        assertFalse(LivePlayback.isLiveType("Channel"))
    }

    @Test
    fun `nested media types and queued live flags do not replace the current item`() {
        val payload = """{"items":[{"MediaSources":[{"Type":"Video","IsLiveStream":true}],"Type":"Episode"},{"Type":"TvChannel","IsLiveStream":true}]}"""
        assertEquals("Episode", LivePlayback.itemType(payload))
        assertFalse(LivePlayback.isLivePayload(payload))
        assertFalse(LivePlayback.isLivePayload("""{"items":[{"Type":"Video"},{"Type":"TvChannel","IsLiveStream":true}]}"""))
        assertTrue(LivePlayback.isLivePayload("""{"items":[{"Type":"Video","IsLiveStream":true}]}"""))
        assertTrue(LivePlayback.isLivePayload("""{"IsLiveStream":true,"items":[{"Type":"Video"}]}"""))
        assertTrue(LivePlayback.isLivePayload("""{"items":[{"Type":"TvChannel"}]}"""))
    }

    @Test
    fun `detects tv channels and infinite media sources`() {
        assertTrue(LivePlayback.isLiveType("TvChannel"))
        assertTrue(LivePlayback.isLiveType("Program"))
        assertFalse(LivePlayback.isLiveType("Movie"))
        assertTrue(LivePlayback.isLiveSource("""{"IsInfiniteStream":true,"Path":"http://s/LiveStreams/1/stream.ts"}"""))
        assertEquals("video/mp2t", LivePlayback.mimeType("mpegts", "http://s/LiveStreams/1/stream.ts"))
        assertEquals("video/mp2t", LivePlayback.mimeType("mpegts", "http://s/LiveTv/LiveStreamFiles/1/stream.ts"))
        assertTrue(LivePlayback.isTunerChannelId("m3u_prosieben"))
        assertFalse(LivePlayback.isUsableLiveSource("""{"Protocol":"File","Path":""}"""))
        assertTrue(LivePlayback.isUsableLiveSource("""{"RequiresOpening":true,"OpenToken":"m3u_1","IsInfiniteStream":true}"""))
        assertEquals("application/x-mpegURL", LivePlayback.mimeType("hls", "http://s/master.m3u8"))
    }
}
