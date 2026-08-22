package org.jellyfin.firetv.shell

import android.webkit.WebView
import org.jellyfin.firetv.core.DisplayScale

object WebViewDisplayFit {
    fun apply(webView: WebView) {
        val width = webView.resources.displayMetrics.widthPixels
        webView.setInitialScale(DisplayScale.initialScalePercent(width))
        webView.settings.textZoom = 100
        webView.settings.useWideViewPort = true
        webView.settings.loadWithOverviewMode = false
        webView.settings.setSupportZoom(false)
        webView.settings.builtInZoomControls = false
        webView.settings.displayZoomControls = false
        webView.scrollBarStyle = WebView.SCROLLBARS_OUTSIDE_OVERLAY
        webView.isScrollbarFadingEnabled = true
        webView.overScrollMode = WebView.OVER_SCROLL_NEVER
    }
}
