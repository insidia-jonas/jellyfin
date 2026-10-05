package org.jellyfin.firetv.core

/** Display metadata stays separate from stream selection and never delays first-frame playback. */
data class PlaybackMetadata(
    val name: String? = null,
    val type: String? = null,
    val seriesName: String? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val episodeEnd: Int? = null,
    val overview: String? = null,
) {
    val isEpisode: Boolean get() = type.equals("Episode", true) || seriesName != null
    val title: String get() = (if (isEpisode) seriesName ?: name else name) ?: "Jellyfin"
    val episodeLine: String? get() {
        if (!isEpisode) return null
        val code = listOfNotNull(
            season?.let { "S" + it.toString().padStart(2, '0') },
            episode?.let { start ->
                "E" + start.toString().padStart(2, '0') +
                    (episodeEnd?.takeIf { it > start }?.let { "–" + it.toString().padStart(2, '0') } ?: "")
            },
        ).joinToString("")
        val episodeTitle = name?.takeUnless {
            it == title || (code.isNotBlank() && Regex("(?i)(folge|episode)\\s+\\d+").matches(it))
        }
        return listOfNotNull(code.takeIf { it.isNotBlank() }, episodeTitle).joinToString(" · ").ifBlank { null }
    }
    val needsLookup: Boolean get() = name == null || type == null || overview == null ||
        (isEpisode && (seriesName == null || season == null || episode == null))

    fun enrichedBy(other: PlaybackMetadata) = PlaybackMetadata(
        other.name ?: name, other.type ?: type, other.seriesName ?: seriesName,
        other.season ?: season, other.episode ?: episode, other.episodeEnd ?: episodeEnd,
        other.overview ?: overview,
    )

    companion object {
        fun fromPayload(payload: String): PlaybackMetadata =
            jsonArrayObjects(payload, "items").firstOrNull()?.let(::fromItem) ?: PlaybackMetadata()

        fun fromItem(item: String): PlaybackMetadata {
            fun text(key: String) = (jsonStringField(item, key) ?: jsonStringField(item, key.replaceFirstChar { it.lowercase() }))
                ?.trim()?.takeIf { it.isNotEmpty() }
            fun number(key: String) = (jsonLongField(item, key) ?: jsonLongField(item, key.replaceFirstChar { it.lowercase() }))
                ?.takeIf { it in 0..Int.MAX_VALUE.toLong() }?.toInt()
            return PlaybackMetadata(
                text("Name"), text("Type"), text("SeriesName"), number("ParentIndexNumber"),
                number("IndexNumber"), number("IndexNumberEnd"), text("Overview"),
            )
        }

        fun fetch(current: ResolvedPlayback, ignoreSslErrors: Boolean, cancellation: HttpCancellation): PlaybackMetadata? {
            if (current.userId.isBlank() || current.isLive) return null
            fun encode(s: String) = java.net.URLEncoder.encode(s, "UTF-8")
            val response = JellyfinHttp.get(
                "${current.serverAddress}/Users/${encode(current.userId)}/Items/${encode(current.itemId)}",
                current.accessToken, ignoreSslErrors, current.deviceId, current.deviceName,
                current.appName, current.appVersion, connectTimeoutMs = 3_000, readTimeoutMs = 3_000,
                cancellation = cancellation,
            )
            if (response.code !in 200..299 || jsonStringField(response.body, "Id") != current.itemId) return null
            return fromItem(response.body)
        }
    }
}
