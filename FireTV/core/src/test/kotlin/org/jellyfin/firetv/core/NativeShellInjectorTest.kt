package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class NativeShellInjectorTest {
    @Test
    fun `inserts nativeshell script immediately after head`() {
        val html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Jellyfin</title></head><body></body></html>"
        val injected = NativeShellInjector.inject(html)
        assertTrue(injected.contains("<script src=\"/native/nativeshell.js\" charset=\"utf-8\"></script>"))
        val headIndex = injected.indexOf("<head>")
        val scriptIndex = injected.indexOf("/native/nativeshell.js")
        val titleIndex = injected.indexOf("<title>")
        assertTrue(headIndex < scriptIndex)
        assertTrue(scriptIndex < titleIndex)
    }

    @Test
    fun `does not inject twice`() {
        val html = "<head><script src=\"/native/nativeshell.js\"></script></head>"
        assertEquals(html, NativeShellInjector.inject(html))
    }

    @Test
    fun `prefixes document when head is missing`() {
        val injected = NativeShellInjector.inject("<html><body>ok</body></html>")
        assertTrue(injected.startsWith("<script src=\"/native/nativeshell.js\""))
    }
}
