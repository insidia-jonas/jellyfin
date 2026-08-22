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
        assertFalse(ResourceKind.isNativeBridge("/web/native-looking.js"))
    }
}
