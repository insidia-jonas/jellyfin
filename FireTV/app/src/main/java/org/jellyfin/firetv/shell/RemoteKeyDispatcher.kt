package org.jellyfin.firetv.shell

import android.view.KeyEvent

object RemoteKeyDispatcher {
    fun javascriptFor(event: KeyEvent): String? {
        if (event.action != KeyEvent.ACTION_DOWN || event.repeatCount > 0) {
            return null
        }
        val key = when (event.keyCode) {
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> "MediaPlayPause"
            KeyEvent.KEYCODE_MEDIA_PLAY -> "MediaPlay"
            KeyEvent.KEYCODE_MEDIA_PAUSE -> "MediaPause"
            KeyEvent.KEYCODE_MEDIA_STOP -> "MediaStop"
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> "MediaFastForward"
            KeyEvent.KEYCODE_MEDIA_REWIND -> "MediaRewind"
            KeyEvent.KEYCODE_MEDIA_NEXT -> "MediaTrackNext"
            KeyEvent.KEYCODE_MEDIA_PREVIOUS -> "MediaTrackPrevious"
            else -> return null
        }
        return "window.FireTvRemote&&window.FireTvRemote.send('$key')"
    }
}
