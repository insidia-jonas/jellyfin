package org.jellyfin.firetv.shell

import android.content.Context
import android.support.v4.media.MediaMetadataCompat
import android.support.v4.media.session.MediaSessionCompat
import android.support.v4.media.session.PlaybackStateCompat
import org.json.JSONObject

class PlaybackMediaSession(context: Context) {
    private val session = MediaSessionCompat(context, "JellyfinFireTV").apply {
        isActive = true
    }

    fun update(json: String) {
        val payload = runCatching { JSONObject(json) }.getOrNull() ?: return
        val title = payload.optString("title").ifBlank { payload.optString("itemName") }
        val artist = payload.optString("artist").ifBlank { payload.optString("album") }
        val durationMs = (payload.optDouble("duration", 0.0) * 1000).toLong()
        val positionMs = (payload.optDouble("position", 0.0) * 1000).toLong()
        val paused = payload.optBoolean("isPaused", payload.optString("playerAction") == "pause")

        session.setMetadata(
            MediaMetadataCompat.Builder()
                .putString(MediaMetadataCompat.METADATA_KEY_TITLE, title)
                .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, artist)
                .putLong(MediaMetadataCompat.METADATA_KEY_DURATION, durationMs)
                .build(),
        )
        val state = if (paused) PlaybackStateCompat.STATE_PAUSED else PlaybackStateCompat.STATE_PLAYING
        session.setPlaybackState(
            PlaybackStateCompat.Builder()
                .setActions(
                    PlaybackStateCompat.ACTION_PLAY or
                        PlaybackStateCompat.ACTION_PAUSE or
                        PlaybackStateCompat.ACTION_PLAY_PAUSE or
                        PlaybackStateCompat.ACTION_STOP or
                        PlaybackStateCompat.ACTION_FAST_FORWARD or
                        PlaybackStateCompat.ACTION_REWIND,
                )
                .setState(state, positionMs, if (paused) 0f else 1f)
                .build(),
        )
    }

    fun hide() {
        session.setPlaybackState(
            PlaybackStateCompat.Builder()
                .setState(PlaybackStateCompat.STATE_STOPPED, 0L, 0f)
                .build(),
        )
    }

    fun release() {
        session.isActive = false
        session.release()
    }
}
