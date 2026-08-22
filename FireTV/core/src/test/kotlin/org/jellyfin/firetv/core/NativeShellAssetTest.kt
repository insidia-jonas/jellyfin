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
        assertTrue(script.contains("htmlvideoautoplay"))
        assertTrue(script.contains("getPlugins: function ()"))
        assertTrue(script.contains("return []"))
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
