package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class PlaybackControlsTest {
    @Test fun `scrubbing previews accelerates bounds and commits only once`() {
        val seek = PlaybackSeek()
        assertEquals(70_000, seek.move(60_000, 300_000, true, 0))
        assertEquals(100_000, seek.move(60_000, 300_000, true, 8))
        assertEquals(160_000, seek.move(60_000, 300_000, true, 20))
        assertEquals(160_000, seek.take())
        assertNull(seek.take())
        assertEquals(299_000, seek.move(295_000, 300_000, true, 0))
        seek.cancel()
        assertEquals(0, seek.move(1_000, 300_000, false, 0))
        seek.cancel()
        assertNull(seek.previewMs)
    }

    @Test fun `PAL content uses 50Hz without changing resolution or rounding NTSC`() {
        fun mode(id: Int, hz: Float, width: Int = 1920) = PlaybackDisplayMode(id, width, 1080, hz)
        val current = mode(1, 59.94f)
        val choices = listOf(current, mode(2, 25f), mode(3, 50f), mode(4, 60f), mode(5, 23.976f), mode(6, 50f, 3840))
        assertEquals(3, PlaybackDisplayMode.choose(25f, current, choices)?.id)
        assertEquals(3, PlaybackDisplayMode.choose(50f, current, choices)?.id)
        assertEquals(1, PlaybackDisplayMode.choose(29.97f, current, choices)?.id)
        assertEquals(4, PlaybackDisplayMode.choose(30f, current, choices)?.id)
        assertEquals(5, PlaybackDisplayMode.choose(23.976f, current, choices)?.id)
        assertNull(PlaybackDisplayMode.choose(24f, current, listOf(mode(6, 48f, 3840))))
        assertNull(PlaybackDisplayMode.choose(-1f, current, choices))
    }

    @Test fun `same language audio variants select exact container track rather than preferred language`() {
        val source = MediaTracks.fromMediaSource("""{"MediaStreams":[
          {"Index":1,"Type":"Audio","Language":"deu","Channels":6},
          {"Index":2,"Type":"Audio","Language":"deu","Channels":6},
          {"Index":3,"Type":"Audio","Language":"eng","Channels":2}]}""")
        val actual = listOf(PlayerAudioTrack("ger", 6), PlayerAudioTrack("de", 6), PlayerAudioTrack("en", 2))
        assertEquals(1, AudioTrackSelection.position(source, 2, actual))
        assertEquals(2, AudioTrackSelection.position(source, 3, actual))
        assertNull(AudioTrackSelection.position(source, 8, actual))
        assertNull(AudioTrackSelection.position(source, 3, actual.take(2)))
    }
}
