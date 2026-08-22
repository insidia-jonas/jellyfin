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
        assertTrue(script.contains("localStorage.setItem(\"layout\", \"tv\")"))
    }

    private fun locateNativeShell(): File {
        val candidates = listOf(
            File("app/src/main/assets/native/nativeshell.js"),
            File("../app/src/main/assets/native/nativeshell.js"),
            File("src/main/assets/native/nativeshell.js"),
        )
        return candidates.first { it.isFile }
    }
}
