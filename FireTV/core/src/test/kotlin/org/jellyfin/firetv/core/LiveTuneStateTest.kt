package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.time.ZoneId

class LiveTuneStateTest {
    @Test
    fun `recovery has a total deadline across slow tune attempts`() {
        var now = 0L
        val state = LiveTuneState { now }
        state.select("one")
        now = 25_000
        assertEquals(1_000L, state.retryDelayMs())
        state.select("one", resetRetries = false)
        assertEquals(20_000L, state.attemptTimeoutMs(25_000))
        now = 45_000
        assertNull(state.retryDelayMs())
        state.stablePlayback()
        now += 600_000
        assertEquals(1_000L, state.retryDelayMs())
        assertEquals(30_000L, state.attemptTimeoutMs(30_000))
    }

    @Test
    fun `guide rollover selects current and next from the requested channel only`() {
        val channel = LiveTvChannel("one", "Channel One", nowTitle = "Old")
        val programs = """{"Items":[
            {"ChannelId":"other","Name":"Other channel","StartDate":"2026-10-01T10:00:00Z","EndDate":"2026-10-01T13:00:00Z"},
            {"ChannelId":"one","Name":"Next","StartDate":"2026-10-01T12:00:00Z","EndDate":"2026-10-01T13:00:00Z"},
            {"ChannelId":"one","Name":"Now","StartDate":"2026-10-01T11:00:00Z","EndDate":"2026-10-01T12:00:00Z"}
        ]}"""
        val now = java.time.Instant.parse("2026-10-01T11:30:00Z").toEpochMilli()
        val guide = LiveTvChannels.withPrograms(channel, programs, now)
        assertEquals("Now", guide.nowTitle)
        assertEquals("Next", guide.nextTitle)
        val rolled = LiveTvChannels.withPrograms(channel, programs, now + 1_800_000)
        assertEquals("Next", rolled.nowTitle)
        assertNull(rolled.nextTitle)
        assertNull(LiveTvChannels.withPrograms(channel, "{\"Items\":[]}", now).nowTitle)
    }

    @Test
    fun `rapid selections reject old completions and retain last selection`() {
        val state = LiveTuneState()
        val first = state.select("one")
        val second = state.select("two")
        val third = state.select("three")
        assertEquals("three", state.selectedId)
        assertFalse(state.accepts(first))
        assertFalse(state.accepts(second))
        assertTrue(state.accepts(third))
        state.invalidate()
        assertFalse(state.accepts(third))
    }

    @Test
    fun `retries back off and stop until stable playback or a new selection`() {
        val state = LiveTuneState()
        state.select("one")
        assertEquals(1000L, state.retryDelayMs())
        state.select("one", resetRetries = false)
        assertEquals(2000L, state.retryDelayMs())
        assertEquals(4000L, state.retryDelayMs())
        assertNull(state.retryDelayMs())
        state.stablePlayback()
        assertEquals(1000L, state.retryDelayMs())
        state.select("two")
        assertEquals(0, state.retries)
    }

    @Test
    fun `EPG converts UTC and offsets including daylight saving time`() {
        val berlin = ZoneId.of("Europe/Berlin")
        assertEquals("22:00", LiveTvChannels.clock("2026-07-01T20:00:00Z", berlin))
        assertEquals("21:00", LiveTvChannels.clock("2026-01-01T20:00:00Z", berlin))
        assertEquals("20:00", LiveTvChannels.clock("2026-07-01T20:00:00+02:00", berlin))
        assertEquals("03:30", LiveTvChannels.clock("2026-03-29T01:30:00Z", berlin))
        assertNull(LiveTvChannels.clock("not a time", berlin))
    }
}
