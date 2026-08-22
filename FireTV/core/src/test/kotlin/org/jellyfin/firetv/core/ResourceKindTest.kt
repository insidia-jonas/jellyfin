package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ResourceKindTest {
    @Test
    fun `video streams are media and must not be intercepted as html`() {
        assertTrue(ResourceKind.isMedia("/Videos/abc/stream"))
        assertTrue(ResourceKind.isMedia("/videos/abc/master.m3u8"))
        assertTrue(ResourceKind.isMedia("/Audio/abc/stream"))
        assertTrue(ResourceKind.isMedia("/Items/abc/Download"))
        assertTrue(ResourceKind.isMedia("/foo/clip.mkv"))
    }

    @Test
    fun `web ui documents are not media`() {
        assertFalse(ResourceKind.isMedia("/web/"))
        assertFalse(ResourceKind.isMedia("/web/index.html"))
        assertFalse(ResourceKind.isMedia("/System/Info/Public"))
    }

    @Test
    fun `native bridge paths are detected`() {
        assertTrue(ResourceKind.isNativeBridge("/native/nativeshell.js"))
        assertTrue(ResourceKind.isNativeBridge("/native/ExoPlayerPlugin.js"))
        assertFalse(ResourceKind.isNativeBridge("/web/native-looking.js"))
    }

    @Test
    fun `only the web index is treated as a document to rewrite`() {
        assertTrue(ResourceKind.isWebDocument("/web"))
        assertTrue(ResourceKind.isWebDocument("/web/"))
        assertTrue(ResourceKind.isWebDocument("/web/index.html"))
        assertFalse(ResourceKind.isWebDocument("/web/main.bundle.js"))
        assertFalse(ResourceKind.isWebDocument("/Videos/1/stream"))
    }

    @Test
    fun `item images are artwork`() {
        assertTrue(ResourceKind.isArtwork("/Items/abc/Images/Primary"))
        assertFalse(ResourceKind.isArtwork("/web/assets/icon.png"))
        assertFalse(ResourceKind.isArtwork("/Videos/abc/stream"))
    }
}
