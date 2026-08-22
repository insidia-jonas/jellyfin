package org.jellyfin.firetv.player

import org.jellyfin.firetv.core.MediaSourceUrls
import org.jellyfin.firetv.core.PlayUrl
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

data class ResolvedPlayback(
    val url: String,
    val title: String,
    val itemId: String,
    val mediaSourceId: String?,
    val playSessionId: String?,
    val startPositionMs: Long,
    val playMethod: String,
    val serverAddress: String,
    val accessToken: String,
    val userId: String,
    val deviceId: String,
    val deviceName: String,
    val appName: String,
    val appVersion: String,
)

object StreamResolver {
    fun resolve(payload: String, ignoreSslErrors: Boolean): ResolvedPlayback {
        val root = JSONObject(payload)
        val items = root.optJSONArray("items") ?: JSONArray()
        require(items.length() > 0) { "No items to play" }
        val item = items.getJSONObject(0)
        val itemId = item.getString("Id")
        val title = item.optString("Name").ifBlank { "Jellyfin" }
        val server = root.getString("serverAddress").trimEnd('/')
        val token = root.optString("accessToken")
        val userId = root.optString("userId")
        val startTicks = root.optLong("startPositionTicks", 0L)
        val mediaSourceId = root.optString("mediaSourceId").takeIf { it.isNotBlank() }

        val body = JSONObject()
            .put("UserId", userId)
            .put("MaxStreamingBitrate", 120_000_000)
            .put("StartTimeTicks", startTicks)
            .put("AutoOpenLiveStream", true)
            .put("EnableDirectPlay", true)
            .put("EnableDirectStream", true)
            .put("EnableTranscoding", true)
            .put("DeviceProfile", DEVICE_PROFILE)
        if (!mediaSourceId.isNullOrBlank()) {
            body.put("MediaSourceId", mediaSourceId)
        }
        if (root.has("audioStreamIndex") && !root.isNull("audioStreamIndex")) {
            body.put("AudioStreamIndex", root.getInt("audioStreamIndex"))
        }
        if (root.has("subtitleStreamIndex") && !root.isNull("subtitleStreamIndex")) {
            body.put("SubtitleStreamIndex", root.getInt("subtitleStreamIndex"))
        }

        val infoUrl = "$server/Items/$itemId/PlaybackInfo?userId=$userId"
        val response = postJson(infoUrl, body.toString(), root, ignoreSslErrors)
        val sources = response.optJSONArray("MediaSources") ?: JSONArray()
        require(sources.length() > 0) { "Server returned no media sources" }
        val source = pickSource(sources, mediaSourceId)
        val urls = MediaSourceUrls(
            transcodingUrl = source.optString("TranscodingUrl").takeIf { it.isNotBlank() },
            directStreamUrl = source.optString("DirectStreamUrl").takeIf { it.isNotBlank() },
            path = source.optString("Path").takeIf { it.isNotBlank() },
            supportsDirectPlay = source.optBoolean("SupportsDirectPlay"),
            supportsDirectStream = source.optBoolean("SupportsDirectStream"),
        )
        val playUrl = PlayUrl.resolve(server, urls) ?: error("No playable URL for this release")
        val playMethod = when {
            urls.supportsDirectPlay && playUrl == urls.path -> "DirectPlay"
            !urls.transcodingUrl.isNullOrBlank() && playUrl.contains(urls.transcodingUrl!!) -> "Transcode"
            else -> "DirectStream"
        }
        return ResolvedPlayback(
            url = playUrl,
            title = title,
            itemId = itemId,
            mediaSourceId = source.optString("Id").takeIf { it.isNotBlank() } ?: mediaSourceId,
            playSessionId = response.optString("PlaySessionId").takeIf { it.isNotBlank() },
            startPositionMs = startTicks / 10_000L,
            playMethod = playMethod,
            serverAddress = server,
            accessToken = token,
            userId = userId,
            deviceId = root.optString("deviceId"),
            deviceName = root.optString("deviceName").ifBlank { "Fire TV" },
            appName = root.optString("appName").ifBlank { "Jellyfin Fire TV" },
            appVersion = root.optString("appVersion").ifBlank { "1.1.0" },
        )
    }

    private fun pickSource(sources: JSONArray, mediaSourceId: String?): JSONObject {
        if (!mediaSourceId.isNullOrBlank()) {
            for (i in 0 until sources.length()) {
                val candidate = sources.getJSONObject(i)
                if (mediaSourceId == candidate.optString("Id")) {
                    return candidate
                }
            }
        }
        return sources.getJSONObject(0)
    }

    private fun postJson(url: String, json: String, auth: JSONObject, ignoreSslErrors: Boolean): JSONObject {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.connectTimeout = 12_000
        connection.readTimeout = 20_000
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "application/json")
        connection.setRequestProperty("Accept", "application/json")
        connection.setRequestProperty("Authorization", authorization(auth))
        val token = auth.optString("accessToken")
        if (token.isNotBlank()) {
            connection.setRequestProperty("X-Emby-Token", token)
        }
        if (ignoreSslErrors && connection is HttpsURLConnection) {
            trustAll(connection)
        }
        connection.outputStream.use { it.write(json.toByteArray(Charsets.UTF_8)) }
        val code = connection.responseCode
        val stream = if (code in 200..299) connection.inputStream else connection.errorStream
        val body = stream?.bufferedReader()?.use { it.readText() }.orEmpty()
        connection.disconnect()
        require(code in 200..299) { "PlaybackInfo failed HTTP $code $body" }
        return JSONObject(body)
    }

    private fun authorization(auth: JSONObject): String {
        val token = auth.optString("accessToken")
        val tokenPart = if (token.isNotBlank()) ", Token=\"$token\"" else ""
        return "MediaBrowser Client=\"${auth.optString("appName", "Jellyfin Fire TV")}\", " +
            "Device=\"${auth.optString("deviceName", "Fire TV")}\", " +
            "DeviceId=\"${auth.optString("deviceId")}\", " +
            "Version=\"${auth.optString("appVersion", "1.1.0")}\"$tokenPart"
    }

    private fun trustAll(connection: HttpsURLConnection) {
        val trustManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(trustManager), SecureRandom())
        connection.sslSocketFactory = context.socketFactory
        connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
    }

    private val DEVICE_PROFILE = JSONObject(
        """
        {
          "Name": "Jellyfin Fire TV ExoPlayer",
          "MaxStreamingBitrate": 120000000,
          "DirectPlayProfiles": [
            {"Container":"mp4,m4v,mov,mkv,webm,ts,mpegts,avi","Type":"Video","VideoCodec":"h264,hevc,vp8,vp9,av1,mpeg2video,mpeg4","AudioCodec":"aac,mp3,ac3,eac3,flac,opus,pcm,dts"},
            {"Container":"mp3,aac,flac,wav,ogg,opus,m4a","Type":"Audio"}
          ],
          "TranscodingProfiles": [
            {"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac,ac3","Protocol":"hls","Context":"Streaming","MaxAudioChannels":"6","MinSegments":"1","BreakOnNonKeyFrames":true}
          ],
          "SubtitleProfiles": [
            {"Format":"vtt","Method":"External"},
            {"Format":"srt","Method":"External"},
            {"Format":"ass","Method":"Encode"}
          ]
        }
        """.trimIndent(),
    )
}
