package org.jellyfin.firetv.shell

import android.webkit.JavascriptInterface
import org.json.JSONObject

class NativeInterface(
    private val host: Host,
) {
    interface Host {
        fun deviceInformation(): JSONObject
        fun exitApp()
        fun openServerSelection()
        fun openClientSettings()
        fun openUrl(url: String)
        fun updateMediaSession(json: String)
        fun hideMediaSession()
        fun enableFullscreen()
        fun disableFullscreen()
        fun updateVolumeLevel(level: Int)
    }

    @JavascriptInterface
    fun getDeviceInformation(): String = host.deviceInformation().toString()

    @JavascriptInterface
    fun enableFullscreen() {
        host.enableFullscreen()
    }

    @JavascriptInterface
    fun disableFullscreen() {
        host.disableFullscreen()
    }

    @JavascriptInterface
    fun openUrl(url: String?) {
        if (!url.isNullOrBlank()) {
            host.openUrl(url)
        }
    }

    @JavascriptInterface
    fun updateMediaSession(json: String?) {
        host.updateMediaSession(json.orEmpty())
    }

    @JavascriptInterface
    fun hideMediaSession() {
        host.hideMediaSession()
    }

    @JavascriptInterface
    fun updateVolumeLevel(value: Int) {
        host.updateVolumeLevel(value)
    }

    @JavascriptInterface
    fun openClientSettings() {
        host.openClientSettings()
    }

    @JavascriptInterface
    fun openServerSelection() {
        host.openServerSelection()
    }

    @JavascriptInterface
    fun exitApp() {
        host.exitApp()
    }
}
