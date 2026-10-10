package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class IptvSourcesTest {
    @Test fun cachedEvidenceKeepsUnknownDistinctFromZeroLatencyAndIncludesPlayingSource() {
        val result = IptvSources.parse("""{"AutomaticMediaSourceId":"base","PlayingSourceId":"am02","State":"PlaybackActive","Sources":[
            {"Id":"am02","MediaSourceId":"base_iptv_hash","Name":"AM02 · Amsterdam","Status":"Reachable","ChannelSpecific":true,"LastCheckedUtc":"2026-10-10T10:20:00Z","MedianStartMilliseconds":1250,"IsDefault":true},
            {"Id":"ro01","MediaSourceId":"base_iptv_other","Name":"RO01","Status":"Unknown","MedianStartMilliseconds":null},
            {"Id":"invalid","Name":"Missing playable id"}]}""")
        assertEquals("base", result.automaticId)
        assertEquals("am02", result.playingId)
        assertEquals(2, result.sources.size)
        assertEquals(1250L, result.sources[0].startMs)
        assertTrue(result.sources[0].channelSpecific)
        assertNull(result.sources[1].startMs)
        assertEquals("Unknown", result.sources[1].status)
    }
}
