package org.jellyfin.firetv

import android.app.Application
import android.webkit.WebView
import org.jellyfin.firetv.BuildConfig

class JellyfinFireTvApp : Application() {
    override fun onCreate() {
        super.onCreate()
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)
    }
}
