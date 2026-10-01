package org.jellyfin.firetv.core

import kotlin.math.floor
import kotlin.math.roundToInt

/**
 * Maps the Fire TV window to the 1920×1080 CSS space jellyfin-web's TV layout uses.
 *
 * WebView.setInitialScale takes a physical-pixel percentage, unlike the viewport
 * meta tag. Dividing by Android density a second time shrinks the Cube's UI.
 * Using DisplayMetrics.widthPixels alone is wrong on a
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
        "width=1920, user-scalable=no, viewport-fit=cover"

    fun pageScalePercent(viewWidthPx: Int): Int {
        if (viewWidthPx <= 0) {
            return 100
        }
        // Round down so the fixed-width layout never extends beyond the view.
        val percent = (viewWidthPx * 100.0) / DESIGN_WIDTH_PX
        return floor(percent).toInt().coerceIn(1, 250)
    }

    fun overscanInsetPx(widthPx: Int, heightPx: Int): Int {
        if (widthPx <= 0 || heightPx <= 0) {
            return 0
        }
        val short = minOf(widthPx, heightPx)
        return (short * 0.03f).roundToInt().coerceIn(12, 64)
    }
}
