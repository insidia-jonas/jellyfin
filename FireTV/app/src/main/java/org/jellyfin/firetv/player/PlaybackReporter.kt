package org.jellyfin.firetv.player

import org.jellyfin.firetv.core.JellyfinHttp
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.PlaybackReportQueue
import org.json.JSONObject

class PlaybackReporter(
    private val playback: ResolvedPlayback,
    private val ignoreSslErrors: Boolean,
) {
    private val queue = PlaybackReportQueue(deliver = { suffix, position, paused, failed ->
        val body = snapshot(paused, position).put("Failed", failed)
        // One bounded retry for the final clock; all reports remain in session order.
        if (!post("/Sessions/Playing$suffix", body) && suffix == "/Stopped") {
            post("/Sessions/Playing$suffix", body)
        }
    })

    fun playing(positionMs: Long = playback.startPositionMs) = queue.playing(positionMs)

    fun progress(positionMs: Long, isPaused: Boolean) {
        queue.progress(positionMs, isPaused)
    }

    fun stopped(positionMs: Long, failed: Boolean = false) {
        queue.stopped(positionMs, failed)
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

    private fun post(path: String, body: JSONObject): Boolean =
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
            ).code in 200..299
        }.getOrDefault(false)
}
