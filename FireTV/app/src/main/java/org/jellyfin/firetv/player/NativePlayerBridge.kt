package org.jellyfin.firetv.player

import android.os.SystemClock
import android.webkit.JavascriptInterface
import org.jellyfin.firetv.shell.NativeInterface

class NativePlayerBridge(
    private val host: NativeInterface.Host,
) {
    @Volatile
    private var lastLaunchAt = 0L

    @Volatile
    private var lastPayload = ""

    @JavascriptInterface
    fun isEnabled(): Boolean = true

    @JavascriptInterface
    fun loadPlayer(args: String?) {
        if (args.isNullOrBlank()) {
            return
        }
        val now = SystemClock.elapsedRealtime()
        if (args == lastPayload && now - lastLaunchAt < 1_500) {
            return
        }
        lastPayload = args
        lastLaunchAt = now
        host.launchPlayer(args)
    }

    @JavascriptInterface
    fun pausePlayer() {
        host.runOnHost { PlayerCommands.pause() }
    }

    @JavascriptInterface
    fun resumePlayer() {
        host.runOnHost { PlayerCommands.resume() }
    }

    @JavascriptInterface
    fun stopPlayer() {
        host.runOnHost { PlayerCommands.stop() }
    }

    @JavascriptInterface
    fun destroyPlayer() {
        host.runOnHost { PlayerCommands.destroy() }
    }

    @JavascriptInterface
    fun seekTicks(ticks: Long) {
        host.runOnHost { PlayerCommands.seekMs(ticks / 10_000L) }
    }

    @JavascriptInterface
    fun seekMs(ms: Long) {
        host.runOnHost { PlayerCommands.seekMs(ms) }
    }

    @JavascriptInterface
    fun setVolume(volume: Int) {
        host.runOnHost { PlayerCommands.setVolume(volume) }
    }

    @JavascriptInterface
    fun setAudioStreamIndex(index: Int) {
        host.runOnHost { PlayerCommands.setAudioStreamIndex(index) }
    }

    @JavascriptInterface
    fun setSubtitleStreamIndex(index: Int) {
        host.runOnHost { PlayerCommands.setSubtitleStreamIndex(index) }
    }
}
