package org.jellyfin.firetv.player

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

class PlaybackReporter(private val playback: ResolvedPlayback) {
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
            .put("CanSeek", true)
            .put("IsPaused", isPaused)
            .put("IsMuted", false)
            .put("PositionTicks", positionMs * 10_000L)
            .put("PlayMethod", playback.playMethod)
            .put("VolumeLevel", 100)
    }

    private fun post(path: String, body: JSONObject) {
        val connection = URL(playback.serverAddress + path).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = 8_000
            connection.readTimeout = 8_000
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.setRequestProperty(
                "Authorization",
                "MediaBrowser Client=\"${playback.appName}\", Device=\"${playback.deviceName}\", " +
                    "DeviceId=\"${playback.deviceId}\", Version=\"${playback.appVersion}\", Token=\"${playback.accessToken}\"",
            )
            if (playback.accessToken.isNotBlank()) {
                connection.setRequestProperty("X-Emby-Token", playback.accessToken)
            }
            connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            connection.responseCode
        } catch (_: Exception) {
            // Progress reporting must never crash playback.
        } finally {
            connection.disconnect()
        }
    }
}
