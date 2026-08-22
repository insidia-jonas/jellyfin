package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class StreamAuthTest {
    @Test
    fun `appends api key when missing`() {
        assertEquals(
            "http://s/Videos/1/stream.mkv?static=true&api_key=tok",
            StreamAuth.withAccessToken("http://s/Videos/1/stream.mkv?static=true", "tok"),
        )
    }

    @Test
    fun `does not duplicate api key`() {
        val url = "http://s/Videos/1/stream?api_key=tok"
        assertEquals(url, StreamAuth.withAccessToken(url, "other"))
    }
}

class DownloadRequestsTest {
    @Test
    fun `parses a single jellyfin-web download payload`() {
        val files = DownloadRequests.parse(
            """{"url":"http://s/Items/1/Download","filename":"Movie.mkv","title":"Movie","itemId":"1"}""",
        )
        assertEquals(1, files.size)
        assertEquals("http://s/Items/1/Download", files[0].url)
        assertEquals("Movie.mkv", files[0].filename)
    }

    @Test
    fun `parses an array of downloads`() {
        val files = DownloadRequests.parse(
            """[{"url":"http://s/a.mp4","filename":"a.mp4"},{"url":"http://s/b.mp4","title":"B"}]""",
        )
        assertEquals(2, files.size)
        assertEquals("a.mp4", DownloadRequests.safeFilename(files[0].filename))
    }

    @Test
    fun `reads access token from a wrapped download envelope`() {
        val parsed = DownloadRequests.parseDetailed(
            """{"accessToken":"tok","files":[{"url":"http://s/Items/1/Download","filename":"Movie.mkv","title":"Movie"}]}""",
        )
        assertEquals("tok", parsed.accessToken)
        assertEquals(1, parsed.files.size)
        assertEquals("Movie.mkv", parsed.files[0].filename)
    }

    @Test
    fun `rejects non http urls and strips path traversal`() {
        assertTrue(DownloadRequests.parse("""{"url":"file:///etc/passwd"}""").isEmpty())
        assertEquals("movie.mkv", DownloadRequests.safeFilename("../../movie.mkv"))
    }
}

class PlaybackPayloadTest {
    @Test
    fun `reads id from items array`() {
        val json = """{"items":[{"Id":"item-1","Name":"Arrival"}],"serverAddress":"http://s:8096"}"""
        assertEquals("item-1", PlaybackPayload.itemId(json))
        assertEquals("Arrival", PlaybackPayload.itemName(json))
        assertEquals("http://s:8096", PlaybackPayload.serverAddress(json))
    }

    @Test
    fun `falls back to ids when items were stripped`() {
        val json = """{"ids":["item-2"],"accessToken":"t","userId":"u"}"""
        assertEquals("item-2", PlaybackPayload.itemId(json))
        assertEquals("t", PlaybackPayload.accessToken(json))
        assertEquals("u", PlaybackPayload.userId(json))
    }
}

class NativeAssetTest {
    @Test
    fun `es modules must be served as text javascript`() {
        assertEquals("text/javascript", NativeAsset.mimeType("ExoPlayerPlugin.js"))
        assertTrue(NativeAsset.responseHeaders("ExoPlayerPlugin.js")["Content-Type"]!!.startsWith("text/javascript"))
    }
}

class ArtworkUrlTest {
    @Test
    fun `downscales oversized tv layout posters`() {
        val original = "http://s:8096/Items/1/Images/Primary?maxWidth=1920&quality=90"
        assertTrue(ArtworkUrl.shouldDownscale(original))
        val resized = ArtworkUrl.downscale(original)
        assertTrue(resized.contains("maxWidth=720"))
        assertFalse(resized.contains("maxWidth=1920"))
    }

    @Test
    fun `leaves already small images alone`() {
        val original = "http://s:8096/Items/1/Images/Primary?maxWidth=400"
        assertFalse(ArtworkUrl.shouldDownscale(original))
    }

    @Test
    fun `caps backdrops higher than posters`() {
        val original = "http://s:8096/Items/1/Images/Backdrop?maxWidth=3840"
        assertTrue(ArtworkUrl.shouldDownscale(original))
        val resized = ArtworkUrl.downscale(original)
        assertTrue(resized.contains("maxWidth=1280"))
        assertFalse(resized.contains("maxWidth=3840"))
    }
}
