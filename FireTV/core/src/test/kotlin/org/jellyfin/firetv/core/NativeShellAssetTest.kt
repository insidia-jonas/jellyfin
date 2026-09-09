package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.File

class NativeShellAssetTest {
    @Test
    fun `nativeshell forces the tv web layout like the iOS shell`() {
        val script = locateNativeShell().readText()
        assertTrue(script.contains("getDefaultLayout"))
        assertTrue(script.contains("return \"tv\""))
        assertTrue(script.contains("width=1920"))
        assertTrue(script.contains("ExoPlayerPlugin"))
        assertTrue(script.contains("filedownload"))
        assertTrue(script.contains("livetv"))
        assertTrue(script.contains("exitmenu"))
        assertTrue(script.contains("findServers"))
        assertTrue(script.contains("FireTvPlayerSync"))
        assertTrue(script.contains("downloadFile"))
        assertTrue(script.contains("accessToken"))
        assertTrue(script.contains("FireTvCanExit"))
        assertFalse(script.contains("rewriteArtwork"))
        assertFalse(script.contains("getScaledImageUrl"))
        assertFalse(script.contains("card:focus"))
        assertFalse(script.contains("0 0 0 3px"))
        assertTrue(script.contains("canSetAudioStreamIndex"))
        assertTrue(script.contains("import(\"/native/ExoPlayerPlugin.js\")") || script.contains("import('/native/ExoPlayerPlugin.js')"))
        assertTrue(script.contains("HTMLVideoElement"))
        assertTrue(script.contains("localStorage.setItem(\"layout\", \"tv\")"))
        assertTrue(script.contains("fitVisualViewport"))
        assertTrue(script.contains("/native/tvExperience.js"))
        assertTrue(script.contains("/native/tv-cinema.css"))
        assertTrue(script.contains("VideoRotation"))
        assertTrue(script.contains("vobsub"))
        assertTrue(script.contains("dvdsub"))
        assertTrue(script.contains("\"2.2.0\"") || script.contains("2.2.0"))
        assertTrue(script.contains("/native/tvLive.js"))
        val plugin = locate("ExoPlayerPlugin.js")
        val pluginText = plugin.readText()
        assertTrue(pluginText.contains("export class ExoPlayerPlugin"))
        assertTrue(pluginText.contains("setAudioStreamIndex"))
        assertTrue(pluginText.contains("setSubtitleStreamIndex"))
        assertTrue(pluginText.contains("nextTrack"))
        assertFalse(pluginText.contains("kind !== \"audio\""))
        val experience = locate("tvExperience.js").readText()
        val cinema = locate("tv-cinema.css").readText()
        assertTrue(experience.contains("Beste Treffer"))
        assertTrue(experience.contains("getImageUrl"))
        assertTrue(experience.contains("isLiveTvProtectedSection"))
        assertTrue(experience.contains("FireTvLive"))
        assertTrue(experience.contains("recursive-on-filter") || experience.contains("Recursive"))
        assertFalse(experience.contains("rewriteArtwork"))
        assertFalse(experience.contains("getScaledImageUrl"))
        assertFalse(experience.contains("HTMLImageElement"))
        assertFalse(experience.contains("card:focus"))
        val live = locate("tvLive.js").readText()
        assertTrue(live.contains("LiveTv/Channels"))
        assertTrue(live.contains("addCurrentProgram"))
        assertTrue(live.contains("NativePlayer"))
        assertTrue(live.contains("IsLiveStream"))
        assertFalse(live.contains("rewriteArtwork"))
        assertFalse(live.contains("getScaledImageUrl"))
        assertFalse(live.contains("HTMLImageElement"))
        assertFalse(live.contains("card:focus"))
        assertTrue(cinema.contains("firetv-live-row"))
        assertFalse(cinema.contains("0 0 0 3px"))
        assertFalse(cinema.contains("card:focus"))
    }

    private fun locateNativeShell(): File = locate("nativeshell.js")

    private fun locate(name: String): File {
        val candidates = listOf(
            File("app/src/main/assets/native/$name"),
            File("../app/src/main/assets/native/$name"),
            File("src/main/assets/native/$name"),
        )
        return candidates.first { it.isFile }
    }
}
