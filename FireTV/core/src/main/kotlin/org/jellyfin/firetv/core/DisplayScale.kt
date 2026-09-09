package org.jellyfin.firetv.core

import kotlin.math.roundToInt

/**
 * Maps the Fire TV window to the 1920×1080 CSS space jellyfin-web's TV layout uses.
 *
 * Chromium WebView: 1 CSS pixel = [density] physical pixels at zoom 1. A 1080p
 * Cube (1920×1080, density 2.0) therefore needs zoom 50% so 1920 CSS fills the
 * panel. Using [android.util.DisplayMetrics.widthPixels] alone is wrong on a
 * 4K-capable Cube plugged into a 1080p TV — the metric can stay 3840 while the
 * activity window is 1920, which made the UI overflow.
 *
 * Consumer 1080p TVs also clip ~3% (overscan). Inset the WebView so cards and
 * the header stay inside the visible panel.
 */
object DisplayScale {
    const val DESIGN_WIDTH_PX: Int = 1920
    const val DESIGN_HEIGHT_PX: Int = 1080

    const val VIEWPORT_CONTENT: String =
        "width=1920, height=1080, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover"

    fun pageScalePercent(viewWidthPx: Int, density: Float): Int {
        if (viewWidthPx <= 0) {
            return 100
        }
        val dpr = if (density < 0.5f) 1f else density
        val percent = (viewWidthPx * 100f) / (DESIGN_WIDTH_PX * dpr)
        return percent.roundToInt().coerceIn(40, 250)
    }

    fun overscanInsetPx(widthPx: Int, heightPx: Int): Int {
        if (widthPx <= 0 || heightPx <= 0) {
            return 0
        }
        val short = minOf(widthPx, heightPx)
        return (short * 0.03f).roundToInt().coerceIn(12, 64)
    }
}
