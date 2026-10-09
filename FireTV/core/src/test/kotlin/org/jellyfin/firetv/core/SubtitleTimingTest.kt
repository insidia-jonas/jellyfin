package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class SubtitleTimingTest {
    @Test fun `offset keys cannot leak across accounts files or tracks`() {
        val key = SubtitleTiming.key("http://server", "user", "film", "source", 2)
        assertEquals(key, SubtitleTiming.key("http://server/", "user", "film", "source", 2))
        assertNotEquals(key, SubtitleTiming.key("http://server", "other", "film", "source", 2))
        assertNotEquals(key, SubtitleTiming.key("http://server", "user", "film", "source", 3))
        assertNotEquals(key, SubtitleTiming.key("http://server", "user", "film", "other", 2))
        assertNotEquals(key, SubtitleTiming.key("http://server", "user", "episode", "source", 2))
        assertEquals(64, key.length)
    }
    @Test fun `offset range is bounded and sign preserved`() {
        assertEquals(500L, SubtitleTiming.clamp(500))
        assertEquals(-500L, SubtitleTiming.clamp(-500))
        assertEquals(120_000L, SubtitleTiming.clamp(Long.MAX_VALUE))
        assertEquals(-120_000L, SubtitleTiming.clamp(Long.MIN_VALUE))
    }
}
