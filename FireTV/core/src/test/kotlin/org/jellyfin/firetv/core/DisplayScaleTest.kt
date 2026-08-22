package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class DisplayScaleTest {
    @Test
    fun `1080p panel is 100 percent so CSS pixels match physical pixels`() {
        assertEquals(100, DisplayScale.initialScalePercent(1920))
    }

    @Test
    fun `4k panel scales the 1920 layout to fill the screen`() {
        assertEquals(200, DisplayScale.initialScalePercent(3840))
    }

    @Test
    fun `narrow width zooms out instead of overflowing`() {
        assertEquals(50, DisplayScale.initialScalePercent(960))
    }
}
