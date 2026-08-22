package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class MediaTracksTest {
    @Test
    fun `parses audio and subtitle streams from a media source`() {
        val source = """
            {
              "Id": "source-1",
              "DefaultAudioStreamIndex": 1,
              "DefaultSubtitleStreamIndex": 3,
              "MediaStreams": [
                {"Index":0,"Type":"Video","Codec":"hevc"},
                {"Index":1,"Type":"Audio","Language":"eng","DisplayTitle":"English AAC","IsDefault":true,"Codec":"aac"},
                {"Index":2,"Type":"Audio","Language":"ger","DisplayTitle":"Deutsch AC3","Codec":"ac3"},
                {"Index":3,"Type":"Subtitle","Language":"ger","DisplayTitle":"German","IsExternal":true,"DeliveryMethod":"External","DeliveryUrl":"/Videos/1/s/Subtitles/3/Stream.vtt"}
              ]
            }
        """.trimIndent()
        val tracks = MediaTracks.fromMediaSource(source)
        assertEquals(3, tracks.size)
        val audio = MediaTracks.audio(tracks)
        val subs = MediaTracks.subtitles(tracks)
        assertEquals(2, audio.size)
        assertEquals(1, subs.size)
        assertEquals("English AAC", audio[0].displayTitle)
        assertTrue(audio[0].isDefault)
        val uri = MediaTracks.sidecarUri("http://s:8096", "item-1", "source-1", subs[0])
        assertEquals("http://s:8096/Videos/1/s/Subtitles/3/Stream.vtt", uri)
        assertEquals("text/vtt", MediaTracks.mimeType(subs[0]))
    }

    @Test
    fun `embedded subtitles without a delivery url are not sidecars`() {
        val track = MediaTrack(
            index = 4,
            type = MediaTrack.Kind.SUBTITLE,
            language = "eng",
            displayTitle = "English",
            codec = "subrip",
            isDefault = false,
            isForced = false,
            isExternal = false,
            deliveryMethod = "Embed",
            deliveryUrl = null,
        )
        assertFalse(track.isTextSidecar)
        assertNull(MediaTracks.sidecarUri("http://s", "1", "1", track))
    }
}

class RemoteSubtitlesTest {
    @Test
    fun `parses OpenSubtitles-style search results`() {
        val body = """
            [
              {"Id":"os-1","Name":"Movie.en.srt","Format":"srt","ProviderName":"Open Subtitles","DownloadCount":1200,"IsHashMatch":true,"ThreeLetterISOLanguageName":"eng"},
              {"Id":"os-2","Name":"Movie.de.srt","Format":"srt","CommunityRating":8.5,"HearingImpaired":true}
            ]
        """.trimIndent()
        val items = RemoteSubtitles.parseSearchBody(body)
        assertEquals(2, items.size)
        assertTrue(items[0].isHashMatch)
        assertTrue(items[0].label().contains("match"))
        assertTrue(items[1].hearingImpaired)
        assertEquals("ger", RemoteSubtitles.preferredLanguage("de-DE"))
        assertEquals("eng", RemoteSubtitles.preferredLanguage("en-US"))
    }

    @Test
    fun `ignores results without an id`() {
        assertTrue(RemoteSubtitles.parseSearchBody("""[{"Name":"nope"}]""").isEmpty())
        assertTrue(RemoteSubtitles.parseSearchBody("[]").isEmpty())
    }
}
