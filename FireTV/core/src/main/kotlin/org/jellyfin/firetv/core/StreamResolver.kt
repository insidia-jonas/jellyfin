package org.jellyfin.firetv.core

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
    val audioTracks: List<MediaTrack> = emptyList(),
    val subtitleTracks: List<MediaTrack> = emptyList(),
    val selectedAudioIndex: Int? = null,
    val selectedSubtitleIndex: Int? = null,
)

/**
 * Asks the Jellyfin server for a playable HTTP URL (direct stream or HLS) and
 * never hands ExoPlayer a server filesystem path or an unauthenticated URL.
 */
object StreamResolver {
    fun resolve(
        payload: String,
        ignoreSslErrors: Boolean,
        audioStreamIndex: Int? = null,
        subtitleStreamIndex: Int? = null,
    ): ResolvedPlayback {
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
        val audioIndex = audioStreamIndex ?: jsonLongField(payload, "audioStreamIndex")?.toInt()
        val subtitleIndex = subtitleStreamIndex ?: jsonLongField(payload, "subtitleStreamIndex")?.toInt()

        val body = buildPlaybackInfoBody(userId, startTicks, mediaSourceId, audioIndex, subtitleIndex)
        val infoUrl = "$server/Items/$itemId/PlaybackInfo?userId=$userId"
        val response = JellyfinHttp.post(
            url = infoUrl,
            body = body,
            accessToken = token,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
        )
        require(response.code in 200..299) {
            "PlaybackInfo failed HTTP ${response.code} ${response.body.take(240)}"
        }
        val sources = jsonArrayObjects(response.body, "MediaSources")
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
        val tracks = MediaTracks.fromMediaSource(source)
        val defaultAudio = audioIndex
            ?: jsonLongField(source, "DefaultAudioStreamIndex")?.toInt()
            ?: MediaTracks.audio(tracks).firstOrNull { it.isDefault }?.index
            ?: MediaTracks.audio(tracks).firstOrNull()?.index
        val defaultSubtitle = subtitleIndex
            ?: jsonLongField(source, "DefaultSubtitleStreamIndex")?.toInt()
        return ResolvedPlayback(
            url = authed,
            title = title,
            itemId = itemId,
            mediaSourceId = jsonStringField(source, "Id") ?: mediaSourceId,
            playSessionId = jsonStringField(response.body, "PlaySessionId"),
            startPositionMs = startTicks / 10_000L,
            playMethod = playMethod,
            serverAddress = server,
            accessToken = token,
            userId = userId,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            audioTracks = MediaTracks.audio(tracks),
            subtitleTracks = MediaTracks.subtitles(tracks),
            selectedAudioIndex = defaultAudio,
            selectedSubtitleIndex = defaultSubtitle,
        )
    }

    private fun pickSource(sources: List<String>, mediaSourceId: String?): String {
        if (!mediaSourceId.isNullOrBlank()) {
            sources.firstOrNull { mediaSourceId == jsonStringField(it, "Id") }?.let { return it }
        }
        return sources.first()
    }

    private fun buildPlaybackInfoBody(
        userId: String,
        startTicks: Long,
        mediaSourceId: String?,
        audioStreamIndex: Int?,
        subtitleStreamIndex: Int?,
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
            audioStreamIndex?.let {
                append("\"AudioStreamIndex\":").append(it).append(',')
            }
            subtitleStreamIndex?.let {
                append("\"SubtitleStreamIndex\":").append(it).append(',')
            }
            if (!mediaSourceId.isNullOrBlank()) {
                append("\"MediaSourceId\":").append(jsonEscape(mediaSourceId)).append(',')
            }
            append("\"DeviceProfile\":").append(DEVICE_PROFILE)
            append('}')
        }
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
            {"Format":"subrip","Method":"External"},
            {"Format":"ttml","Method":"External"},
            {"Format":"ass","Method":"External"},
            {"Format":"ssa","Method":"Encode"},
            {"Format":"pgssub","Method":"Encode"}
          ]
        }
    """.trimIndent()
}
