package org.jellyfin.firetv.core

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

/**
 * Asks the Jellyfin server for a playable HTTP URL (direct stream or HLS) and
 * never hands ExoPlayer a server filesystem path or an unauthenticated URL.
 */
object StreamResolver {
    fun resolve(payload: String, ignoreSslErrors: Boolean): ResolvedPlayback {
        val itemId = PlaybackPayload.itemId(payload) ?: error("No items to play")
        val title = PlaybackPayload.itemName(payload)
        val server = PlaybackPayload.serverAddress(payload) ?: error("Missing server address")
        val token = PlaybackPayload.accessToken(payload)
        val userId = PlaybackPayload.userId(payload)
        val startTicks = jsonLongField(payload, "startPositionTicks") ?: 0L
        val mediaSourceId = jsonStringField(payload, "mediaSourceId")
            ?: jsonStringField(payload, "MediaSourceId")
        val deviceId = jsonStringField(payload, "deviceId").orEmpty()
        val deviceName = jsonStringField(payload, "deviceName")?.ifBlank { null } ?: "Fire TV"
        val appName = jsonStringField(payload, "appName")?.ifBlank { null } ?: FireTvClient.APP_NAME
        val appVersion = jsonStringField(payload, "appVersion")?.ifBlank { null } ?: FireTvClient.APP_VERSION

        val body = buildPlaybackInfoBody(payload, userId, startTicks, mediaSourceId)
        val infoUrl = "$server/Items/$itemId/PlaybackInfo?userId=$userId"
        val response = postJson(infoUrl, body, payload, ignoreSslErrors)
        val sources = jsonArrayObjects(response, "MediaSources")
        require(sources.isNotEmpty()) { "Server returned no media sources" }
        val source = pickSource(sources, mediaSourceId)
        val urls = MediaSourceUrls(
            transcodingUrl = jsonStringField(source, "TranscodingUrl"),
            directStreamUrl = jsonStringField(source, "DirectStreamUrl"),
            path = jsonStringField(source, "Path"),
            supportsDirectPlay = jsonBooleanField(source, "SupportsDirectPlay") ?: false,
            supportsDirectStream = jsonBooleanField(source, "SupportsDirectStream") ?: false,
        )
        val playUrl = PlayUrl.resolve(server, urls) ?: error("No playable URL for this release")
        val authed = StreamAuth.withAccessToken(playUrl, token)
        val playMethod = when {
            urls.supportsDirectPlay && playUrl == urls.path -> "DirectPlay"
            !urls.transcodingUrl.isNullOrBlank() && playUrl.contains(urls.transcodingUrl!!) -> "Transcode"
            else -> "DirectStream"
        }
        return ResolvedPlayback(
            url = authed,
            title = title,
            itemId = itemId,
            mediaSourceId = jsonStringField(source, "Id") ?: mediaSourceId,
            playSessionId = jsonStringField(response, "PlaySessionId"),
            startPositionMs = startTicks / 10_000L,
            playMethod = playMethod,
            serverAddress = server,
            accessToken = token,
            userId = userId,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
        )
    }

    private fun pickSource(sources: List<String>, mediaSourceId: String?): String {
        if (!mediaSourceId.isNullOrBlank()) {
            sources.firstOrNull { mediaSourceId == jsonStringField(it, "Id") }?.let { return it }
        }
        return sources.first()
    }

    private fun buildPlaybackInfoBody(
        payload: String,
        userId: String,
        startTicks: Long,
        mediaSourceId: String?,
    ): String {
        return buildString {
            append('{')
            append("\"UserId\":").append(jsonEscape(userId)).append(',')
            append("\"MaxStreamingBitrate\":120000000,")
            append("\"StartTimeTicks\":").append(startTicks).append(',')
            append("\"AutoOpenLiveStream\":true,")
            append("\"EnableDirectPlay\":true,")
            append("\"EnableDirectStream\":true,")
            append("\"EnableTranscoding\":true,")
            jsonLongField(payload, "audioStreamIndex")?.let {
                append("\"AudioStreamIndex\":").append(it).append(',')
            }
            jsonLongField(payload, "subtitleStreamIndex")?.let {
                append("\"SubtitleStreamIndex\":").append(it).append(',')
            }
            if (!mediaSourceId.isNullOrBlank()) {
                append("\"MediaSourceId\":").append(jsonEscape(mediaSourceId)).append(',')
            }
            append("\"DeviceProfile\":").append(DEVICE_PROFILE)
            append('}')
        }
    }

    private fun postJson(url: String, json: String, auth: String, ignoreSslErrors: Boolean): String {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.connectTimeout = 12_000
        connection.readTimeout = 20_000
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "application/json")
        connection.setRequestProperty("Accept", "application/json")
        connection.setRequestProperty("Authorization", authorization(auth))
        val token = PlaybackPayload.accessToken(auth)
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
        require(code in 200..299) { "PlaybackInfo failed HTTP $code ${body.take(240)}" }
        return body
    }

    private fun authorization(auth: String): String {
        val token = PlaybackPayload.accessToken(auth)
        val tokenPart = if (token.isNotBlank()) ", Token=\"$token\"" else ""
        val appName = jsonStringField(auth, "appName") ?: FireTvClient.APP_NAME
        val deviceName = jsonStringField(auth, "deviceName") ?: "Fire TV"
        val deviceId = jsonStringField(auth, "deviceId").orEmpty()
        val appVersion = jsonStringField(auth, "appVersion") ?: FireTvClient.APP_VERSION
        return "MediaBrowser Client=\"$appName\", Device=\"$deviceName\", DeviceId=\"$deviceId\", Version=\"$appVersion\"$tokenPart"
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

    private val DEVICE_PROFILE = """
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
    """.trimIndent()
}
