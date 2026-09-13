package org.jellyfin.firetv.core

/**
 * Audio and subtitle streams from a Jellyfin MediaSource, used by the Fire TV
 * player OSD so the remote can switch tracks without opening jellyfin-web.
 */
data class MediaTrack(
    val index: Int,
    val type: Kind,
    val language: String?,
    val displayTitle: String,
    val codec: String?,
    val isDefault: Boolean,
    val isForced: Boolean,
    val isExternal: Boolean,
    val deliveryMethod: String?,
    val deliveryUrl: String?,
) {
    enum class Kind { AUDIO, SUBTITLE, OTHER }

    val isTextSidecar: Boolean
        get() {
            if (type != Kind.SUBTITLE) {
                return false
            }
            val method = deliveryMethod?.lowercase()
            if (method == "embed" || method == "encode" || method == "drop") {
                return !deliveryUrl.isNullOrBlank()
            }
            return true
        }
}

object MediaTracks {
    fun fromMediaSource(sourceJson: String): List<MediaTrack> {
        return jsonArrayObjects(sourceJson, "MediaStreams").mapNotNull { parseOne(it) }
    }

    fun audio(tracks: List<MediaTrack>): List<MediaTrack> = tracks.filter { it.type == MediaTrack.Kind.AUDIO }

    fun subtitles(tracks: List<MediaTrack>): List<MediaTrack> = tracks.filter { it.type == MediaTrack.Kind.SUBTITLE }

    fun sidecarUri(
        serverAddress: String,
        itemId: String,
        mediaSourceId: String?,
        track: MediaTrack,
    ): String? {
        if (!track.isTextSidecar) {
            return null
        }
        if (!track.deliveryUrl.isNullOrBlank()) {
            return PlayUrl.absolutize(serverAddress, track.deliveryUrl!!)
        }
        val source = mediaSourceId?.ifBlank { null } ?: itemId
        val format = sidecarExtension(track)
        return "${serverAddress.trimEnd('/')}/Videos/$itemId/$source/Subtitles/${track.index}/Stream.$format"
    }

    fun mimeType(track: MediaTrack): String {
        val hint = listOfNotNull(track.codec, track.deliveryUrl, track.displayTitle)
            .joinToString(" ")
            .lowercase()
        return when {
            hint.contains("vtt") || hint.contains("webvtt") -> "text/vtt"
            hint.contains("ass") || hint.contains("ssa") -> "text/x-ssa"
            hint.contains("ttml") -> "application/ttml+xml"
            else -> "application/x-subrip"
        }
    }

    private fun sidecarExtension(track: MediaTrack): String {
        return when (mimeType(track)) {
            "text/vtt" -> "vtt"
            "text/x-ssa" -> "ass"
            "application/ttml+xml" -> "ttml"
            else -> "srt"
        }
    }

    private fun parseOne(json: String): MediaTrack? {
        val type = when (jsonStringField(json, "Type")?.lowercase()) {
            "audio" -> MediaTrack.Kind.AUDIO
            "subtitle" -> MediaTrack.Kind.SUBTITLE
            else -> return null
        }
        val index = jsonLongField(json, "Index")?.toInt() ?: return null
        val language = jsonStringField(json, "Language")?.ifBlank { null }
        val title = jsonStringField(json, "DisplayTitle")
            ?: jsonStringField(json, "Title")
            ?: language
            ?: if (type == MediaTrack.Kind.AUDIO) "Audio $index" else "Subtitle $index"
        return MediaTrack(
            index = index,
            type = type,
            language = language,
            displayTitle = title,
            codec = jsonStringField(json, "Codec") ?: jsonStringField(json, "CodecTag"),
            isDefault = jsonBooleanField(json, "IsDefault") == true,
            isForced = jsonBooleanField(json, "IsForced") == true,
            isExternal = jsonBooleanField(json, "IsExternal") == true,
            deliveryMethod = jsonStringField(json, "DeliveryMethod"),
            deliveryUrl = jsonStringField(json, "DeliveryUrl"),
        )
    }
}
