package org.jellyfin.firetv.core

import java.util.Locale
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class TrackPresentationTest {
    private fun track(fields: String): MediaTrack = MediaTracks.fromMediaSource("""{"MediaStreams":[{"Index":2,"Type":"Audio",$fields}]}""").single()

    @Test
    fun `bibliographic and modern language codes produce the same readable label`() {
        for (code in listOf("ger", "deu", "de")) {
            assertEquals("Deutsch", TrackPresentation.language(track(""""Language":"$code""""), Locale.GERMAN))
        }
        assertEquals("Englisch", TrackPresentation.language(track(""""Language":"eng""""), Locale.GERMAN))
    }

    @Test
    fun `unknown languages are not inferred from release names`() {
        assertNull(TrackPresentation.language(track(""""Language":"und","DisplayTitle":"German.Release.English.1080p""""), Locale.GERMAN))
        assertNull(TrackPresentation.language(track(""""Codec":"aac""""), Locale.GERMAN))
    }

    @Test
    fun `explicit leading language label repairs missing container language`() {
        assertEquals("Deutsch", TrackPresentation.language(track(""""DisplayTitle":"Deutsch Stereo - Line by LiNEUP - AAC - Standard""""), Locale.GERMAN))
        assertEquals("Englisch", TrackPresentation.language(track(""""Language":"und","DisplayTitle":"English ASS""""), Locale.GERMAN))
        assertNull(TrackPresentation.language(track(""""DisplayTitle":"My German Release""""), Locale.GERMAN))
    }

    @Test
    fun `channel layout comes from stream metadata rather than the release title`() {
        val audio = track(""""Codec":"eac3","Channels":6,"DisplayTitle":"Atmos 7.1 filename"""")
        assertEquals("Dolby Digital Plus · 5.1", TrackPresentation.audioFormat(audio))
        assertEquals("AAC", TrackPresentation.audioFormat(track(""""Codec":"aac","DisplayTitle":"Stereo"""")))
    }

    @Test
    fun `subtitle attributes require whole words rather than filename fragments`() {
        assertTrue(TrackPresentation.isSynchronized(track(""""DisplayTitle":"Synchronisiert-Tonspur-2 - English"""")))
        assertTrue(TrackPresentation.isAiGenerated(track(""""DisplayTitle":"KI - Deutsch"""")))
        assertFalse(TrackPresentation.isAiGenerated(track(""""DisplayTitle":"Hawaii - English"""")))
        assertTrue(TrackPresentation.isHearingImpaired(track(""""DisplayTitle":"English SDH"""")))
        assertTrue(TrackPresentation.isCommentary(track(""""DisplayTitle":"Director commentary"""")))
    }
}
