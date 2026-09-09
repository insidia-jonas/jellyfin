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
    val isLive: Boolean = false,
    val liveStreamId: String? = null,
    val container: String? = null,
    val isAudio: Boolean = false,
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
        val requestedId = PlaybackPayload.itemId(payload) ?: error("No items to play")
        val tunerId = LivePlayback.tunerChannelId(payload)
        val title = LiveTvNowNextText.channelTitle(
            PlaybackPayload.itemName(payload),
            PlaybackPayload.itemOriginalTitle(payload),
            PlaybackPayload.itemOverview(payload),
        ).ifBlank { PlaybackPayload.itemName(payload) }
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
        val liveHint = LivePlayback.isLivePayload(payload)

        val body = buildPlaybackInfoBody(userId, startTicks, mediaSourceId, audioIndex, subtitleIndex)
        var itemId = requestedId
        var response = playbackInfo(server, itemId, userId, body, token, ignoreSslErrors, deviceId, deviceName, appName, appVersion)
        if (response.code !in 200..299 && !tunerId.isNullOrBlank() && tunerId != itemId) {
            itemId = tunerId
            response = playbackInfo(server, itemId, userId, body, token, ignoreSslErrors, deviceId, deviceName, appName, appVersion)
        }
        require(response.code in 200..299) {
            "PlaybackInfo failed HTTP ${response.code} ${response.body.take(240)}"
        }
        jsonStringField(response.body, "ErrorCode")?.takeIf { it.isNotBlank() }?.let { code ->
            error("PlaybackInfo $code")
        }
        val sources = jsonArrayObjects(response.body, "MediaSources")
        require(sources.isNotEmpty()) { "Server returned no media sources" }
        var source = pickSource(sources, mediaSourceId, liveHint)
        val playSessionId = jsonStringField(response.body, "PlaySessionId")
        val live = liveHint || LivePlayback.isLiveSource(source)
        val openToken = liveOpenToken(source, tunerId, itemId)
        if (live && (needsLiveOpen(source) || PlayUrl.resolveLive(server, urlsOf(source)) == null)) {
            openLiveStream(
                server = server,
                itemId = itemId,
                userId = userId,
                token = token,
                ignoreSslErrors = ignoreSslErrors,
                deviceId = deviceId,
                deviceName = deviceName,
                appName = appName,
                appVersion = appVersion,
                mediaSourceId = jsonStringField(source, "Id") ?: mediaSourceId,
                playSessionId = playSessionId,
                openToken = openToken,
            )?.let { opened ->
                source = opened
            }
        }
        val urls = urlsOf(source)
        val playUrl = if (live) {
            PlayUrl.resolveLive(server, urls) ?: error("No playable live URL from the Jellyfin proxy")
        } else {
            PlayUrl.resolve(server, urls) ?: error("No playable URL for this release")
        }
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
            isLive = live || LivePlayback.isLive(payload, source),
            liveStreamId = jsonStringField(source, "LiveStreamId"),
            container = jsonStringField(source, "Container"),
            isAudio = PlaybackPayload.isAudio(payload),
        )
    }

    fun closeLiveStream(playback: ResolvedPlayback, ignoreSslErrors: Boolean) {
        val liveStreamId = playback.liveStreamId?.ifBlank { null } ?: return
        runCatching {
            JellyfinHttp.post(
                url = "${playback.serverAddress}/LiveStreams/Close?liveStreamId=$liveStreamId",
                body = "{}",
                accessToken = playback.accessToken,
                ignoreSslErrors = ignoreSslErrors,
                deviceId = playback.deviceId,
                deviceName = playback.deviceName,
                appName = playback.appName,
                appVersion = playback.appVersion,
            )
        }
    }

    private fun needsLiveOpen(source: String): Boolean {
        return jsonBooleanField(source, "RequiresOpening") == true &&
            jsonStringField(source, "LiveStreamId").isNullOrBlank()
    }

    private fun openLiveStream(
        server: String,
        itemId: String,
        userId: String,
        token: String,
        ignoreSslErrors: Boolean,
        deviceId: String,
        deviceName: String,
        appName: String,
        appVersion: String,
        mediaSourceId: String?,
        playSessionId: String?,
        openToken: String?,
    ): String? {
        val body = buildString {
            append('{')
            append("\"ItemId\":").append(jsonEscape(itemId)).append(',')
            append("\"UserId\":").append(jsonEscape(userId)).append(',')
            append("\"MaxStreamingBitrate\":120000000,")
            append("\"EnableDirectPlay\":true,")
            append("\"EnableDirectStream\":true,")
            if (!mediaSourceId.isNullOrBlank()) {
                append("\"MediaSourceId\":").append(jsonEscape(mediaSourceId)).append(',')
            }
            if (!playSessionId.isNullOrBlank()) {
                append("\"PlaySessionId\":").append(jsonEscape(playSessionId)).append(',')
            }
            if (!openToken.isNullOrBlank()) {
                append("\"OpenToken\":").append(jsonEscape(openToken)).append(',')
            }
            append("\"DeviceProfile\":").append(DeviceProfile.JSON)
            append('}')
        }
        val response = JellyfinHttp.post(
            url = "$server/LiveStreams/Open",
            body = body,
            accessToken = token,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            connectTimeoutMs = 15_000,
            readTimeoutMs = 35_000,
        )
        if (response.code !in 200..299) {
            return null
        }
        return jsonObjectField(response.body, "MediaSource")
    }

    private fun playbackInfo(
        server: String,
        itemId: String,
        userId: String,
        body: String,
        token: String,
        ignoreSslErrors: Boolean,
        deviceId: String,
        deviceName: String,
        appName: String,
        appVersion: String,
    ): JellyfinHttp.Response {
        return JellyfinHttp.post(
            url = "$server/Items/$itemId/PlaybackInfo?userId=$userId",
            body = body,
            accessToken = token,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            connectTimeoutMs = 15_000,
            readTimeoutMs = 35_000,
        )
    }

    private fun liveOpenToken(source: String, tunerId: String?, itemId: String): String? {
        val fromSource = jsonStringField(source, "OpenToken")
        return listOfNotNull(fromSource, tunerId, itemId).firstOrNull { LivePlayback.isTunerChannelId(it) }
            ?: fromSource
            ?: tunerId
    }

    private fun urlsOf(source: String): MediaSourceUrls {
        return MediaSourceUrls(
            transcodingUrl = jsonStringField(source, "TranscodingUrl"),
            directStreamUrl = jsonStringField(source, "DirectStreamUrl"),
            path = jsonStringField(source, "Path"),
            supportsDirectPlay = jsonBooleanField(source, "SupportsDirectPlay") ?: false,
            supportsDirectStream = jsonBooleanField(source, "SupportsDirectStream") ?: false,
        )
    }

    private fun pickSource(sources: List<String>, mediaSourceId: String?, live: Boolean): String {
        if (!mediaSourceId.isNullOrBlank()) {
            sources.firstOrNull { mediaSourceId == jsonStringField(it, "Id") }?.let { match ->
                if (!live || LivePlayback.isUsableLiveSource(match)) {
                    return match
                }
            }
        }
        if (live) {
            sources.firstOrNull { LivePlayback.isUsableLiveSource(it) }?.let { return it }
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
            append("\"DeviceProfile\":").append(DeviceProfile.JSON)
            append('}')
        }
    }
}
