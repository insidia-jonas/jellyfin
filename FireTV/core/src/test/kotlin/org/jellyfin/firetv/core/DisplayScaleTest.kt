package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class DisplayScaleTest {
    @Test
    fun `1080p with density 1 maps CSS pixels 1-to-1`() {
        assertEquals(100, DisplayScale.pageScalePercent(1920))
    }

    @Test
    fun `1080p Cube fits the available window after overscan without dividing by density`() {
        val width = 1920 - 2 * DisplayScale.overscanInsetPx(1920, 1080)
        val scale = DisplayScale.pageScalePercent(width)
        assertEquals(96, scale)
        org.junit.jupiter.api.Assertions.assertTrue(DisplayScale.DESIGN_WIDTH_PX * scale / 100.0 <= width)
    }

    @Test
    fun `4k density 2 keeps the 1920 layout filling the window`() {
        assertEquals(200, DisplayScale.pageScalePercent(3840))
    }

    @Test
    fun `720p fits without rounding up and clipping`() {
        assertEquals(66, DisplayScale.pageScalePercent(1280))
    }

    @Test
    fun `unknown width has a safe initial scale`() {
        assertEquals(100, DisplayScale.pageScalePercent(0))
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
