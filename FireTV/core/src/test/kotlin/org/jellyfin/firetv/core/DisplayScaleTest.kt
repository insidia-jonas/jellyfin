package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class DisplayScaleTest {
    @Test
    fun `1080p with density 1 maps CSS pixels 1-to-1`() {
        assertEquals(100, DisplayScale.pageScalePercent(1920, 1f))
    }

    @Test
    fun `1080p Fire TV Cube density 2 zooms out so 1920 CSS fills the panel`() {
        assertEquals(50, DisplayScale.pageScalePercent(1920, 2f))
    }

    @Test
    fun `4k density 2 keeps the 1920 layout filling the window`() {
        assertEquals(100, DisplayScale.pageScalePercent(3840, 2f))
    }

    @Test
    fun `4k density 1 scales the 1920 layout up`() {
        assertEquals(200, DisplayScale.pageScalePercent(3840, 1f))
    }

    @Test
    fun `overscan inset is about 3 percent on a 1080p panel`() {
        assertEquals(32, DisplayScale.overscanInsetPx(1920, 1080))
    }

    @Test
    fun `overscan inset is capped on 4k`() {
        assertEquals(64, DisplayScale.overscanInsetPx(3840, 2160))
    }
}
