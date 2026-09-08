package org.jellyfin.firetv.core

/**
 * Detects IPTV / Live TV items from the M3U tuner path (TvChannel, Program,
 * infinite MediaSources) so ExoPlayer can skip VOD seeking and keep the
 * server live stream open.
 */
object LivePlayback {
    fun itemType(payload: String): String? {
        val fromItem = jsonArrayObjects(payload, "items").firstOrNull()?.let {
            jsonStringField(it, "Type") ?: jsonStringField(it, "type")
        }
        return fromItem?.ifBlank { null } ?: jsonStringField(payload, "itemType")
    }

    fun isLivePayload(payload: String): Boolean {
        if (jsonBooleanField(payload, "IsLiveStream") == true) {
            return true
        }
        val item = jsonArrayObjects(payload, "items").firstOrNull()
        if (item != null && jsonBooleanField(item, "IsLiveStream") == true) {
            return true
        }
        return isLiveType(itemType(payload))
    }

    fun isLiveType(type: String?): Boolean {
        return when (type?.lowercase()) {
            "tvchannel", "program", "livetvprogram" -> true
            else -> false
        }
    }

    fun isLiveSource(source: String): Boolean {
        return jsonBooleanField(source, "IsInfiniteStream") == true ||
            !jsonStringField(source, "LiveStreamId").isNullOrBlank()
    }

    fun isLive(payload: String, source: String?): Boolean {
        return isLivePayload(payload) || (source != null && isLiveSource(source))
    }

    fun mimeType(container: String?, url: String): String? {
        val kind = container?.lowercase().orEmpty()
        val path = url.lowercase()
        return when {
            kind == "hls" || path.contains(".m3u8") -> "application/x-mpegURL"
            kind == "mpegts" || kind == "ts" || path.contains("mpegts") ||
                path.contains("stream.ts") || path.contains("/livestreams/") -> "video/mp2t"
            else -> null
        }
    }
}
