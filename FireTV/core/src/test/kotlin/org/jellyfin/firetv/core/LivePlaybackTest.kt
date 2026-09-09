package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class LivePlaybackTest {
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
