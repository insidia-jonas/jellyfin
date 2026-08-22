package org.jellyfin.firetv.player

import android.webkit.JavascriptInterface
import org.jellyfin.firetv.shell.NativeInterface

class NativePlayerBridge(
    private val host: NativeInterface.Host,
) {
    @JavascriptInterface
    fun isEnabled(): Boolean = true

    @JavascriptInterface
    fun loadPlayer(args: String?) {
        if (!args.isNullOrBlank()) {
            host.launchPlayer(args)
        }
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
}
