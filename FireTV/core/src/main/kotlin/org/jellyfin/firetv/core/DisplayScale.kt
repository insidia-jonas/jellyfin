package org.jellyfin.firetv.core

/**
 * Maps a Fire TV display to the 1920x1080 CSS coordinate space jellyfin-web's TV layout
 * is designed for. WebView otherwise uses the device density (often 2.0), so a 1080p
 * panel is reported as ~960 CSS pixels and the UI is drawn twice as large.
 */
object DisplayScale {
    const val DESIGN_WIDTH_PX: Int = 1920
    const val DESIGN_HEIGHT_PX: Int = 1080

    fun initialScalePercent(widthPixels: Int): Int {
        if (widthPixels <= 0) {
            return 100
        }
        return ((widthPixels * 100f) / DESIGN_WIDTH_PX).toInt().coerceIn(50, 250)
    }

    const val VIEWPORT_CONTENT: String =
        "width=1920, height=1080, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover"
}
