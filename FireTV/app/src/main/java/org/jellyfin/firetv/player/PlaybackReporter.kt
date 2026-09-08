package org.jellyfin.firetv.player

import org.jellyfin.firetv.core.JellyfinHttp
import org.jellyfin.firetv.core.ResolvedPlayback
import org.json.JSONObject

class PlaybackReporter(
    private val playback: ResolvedPlayback,
    private val ignoreSslErrors: Boolean,
) {
    fun playing() = post("/Sessions/Playing", snapshot(isPaused = false))

    fun progress(positionMs: Long, isPaused: Boolean) {
        post("/Sessions/Playing/Progress", snapshot(isPaused = isPaused, positionMs = positionMs))
    }

    fun stopped(positionMs: Long) {
        post("/Sessions/Playing/Stopped", snapshot(isPaused = true, positionMs = positionMs))
    }

    private fun snapshot(isPaused: Boolean, positionMs: Long = playback.startPositionMs): JSONObject {
        return JSONObject()
            .put("ItemId", playback.itemId)
            .put("MediaSourceId", playback.mediaSourceId)
            .put("PlaySessionId", playback.playSessionId)
            .put("CanSeek", !playback.isLive)
            .put("IsPaused", isPaused)
            .put("IsMuted", false)
            .put("PositionTicks", positionMs * 10_000L)
            .put("PlayMethod", playback.playMethod)
            .put("VolumeLevel", 100)
            .apply {
                playback.liveStreamId?.takeIf { it.isNotBlank() }?.let { put("LiveStreamId", it) }
            }
    }

    private fun post(path: String, body: JSONObject) {
        runCatching {
            JellyfinHttp.post(
                url = playback.serverAddress + path,
                body = body.toString(),
                accessToken = playback.accessToken,
                ignoreSslErrors = ignoreSslErrors,
                deviceId = playback.deviceId,
                deviceName = playback.deviceName,
                appName = playback.appName,
                appVersion = playback.appVersion,
                connectTimeoutMs = 8_000,
                readTimeoutMs = 8_000,
            )
        }
    }
}
