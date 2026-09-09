package org.jellyfin.firetv.shell

import android.content.Context
import android.graphics.Point
import android.os.Build
import android.view.ViewGroup
import android.view.WindowManager
import android.webkit.WebView
import org.jellyfin.firetv.core.DisplayScale

object WebViewDisplayFit {
    fun apply(webView: WebView) {
        val (windowW, windowH) = windowSizePx(webView.context)
        val inset = DisplayScale.overscanInsetPx(windowW, windowH)
        val lp = webView.layoutParams
        if (lp is ViewGroup.MarginLayoutParams) {
            lp.setMargins(inset, inset, inset, inset)
            webView.layoutParams = lp
        }

        webView.settings.textZoom = 100
        webView.settings.useWideViewPort = true
        webView.settings.loadWithOverviewMode = false
        webView.settings.setSupportZoom(false)
        webView.settings.builtInZoomControls = false
        webView.settings.displayZoomControls = false
        webView.scrollBarStyle = WebView.SCROLLBARS_OUTSIDE_OVERLAY
        webView.isScrollbarFadingEnabled = true
        webView.overScrollMode = WebView.OVER_SCROLL_NEVER
        webView.isVerticalScrollBarEnabled = false
        webView.isHorizontalScrollBarEnabled = false

        fun syncScale() {
            val laidOut = webView.width
            val width = if (laidOut > 0) {
                laidOut
            } else {
                (windowW - 2 * inset).coerceAtLeast(1)
            }
            val density = webView.resources.displayMetrics.density
            webView.setInitialScale(DisplayScale.pageScalePercent(width, density))
        }

        var lastWidth = -1
        webView.addOnLayoutChangeListener { _, left, _, right, _, _, _, _, _ ->
            val width = right - left
            if (width > 0 && width != lastWidth) {
                lastWidth = width
                syncScale()
            }
        }
        syncScale()
        webView.post { syncScale() }
    }

    private fun windowSizePx(context: Context): Pair<Int, Int> {
        val wm = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = wm.currentWindowMetrics.bounds
            return bounds.width() to bounds.height()
        }
        val point = Point()
        @Suppress("DEPRECATION")
        wm.defaultDisplay.getRealSize(point)
        return point.x to point.y
    }
}
