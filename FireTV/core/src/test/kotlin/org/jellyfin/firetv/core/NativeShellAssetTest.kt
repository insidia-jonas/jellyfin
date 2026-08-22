package org.jellyfin.firetv.core

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
        assertTrue(script.contains("downloadFile"))
        assertTrue(script.contains("accessToken"))
        assertTrue(script.contains("FireTvCanExit"))
        assertTrue(script.contains("rewriteArtwork"))
        assertTrue(script.contains("canSetAudioStreamIndex"))
        assertTrue(script.contains("import(\"/native/ExoPlayerPlugin.js\")") || script.contains("import('/native/ExoPlayerPlugin.js')"))
        assertTrue(script.contains("HTMLVideoElement"))
        assertTrue(script.contains("localStorage.setItem(\"layout\", \"tv\")"))
        val plugin = locate("ExoPlayerPlugin.js")
        val pluginText = plugin.readText()
        assertTrue(pluginText.contains("export class ExoPlayerPlugin"))
        assertTrue(pluginText.contains("setAudioStreamIndex"))
        assertTrue(pluginText.contains("setSubtitleStreamIndex"))
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
