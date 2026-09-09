package org.jellyfin.firetv.core

/**
 * Detects IPTV / Live TV items from the official TvChannel path and the
 * Live TV library channel (IChannel tiles with IsLiveStream).
 *
 * Playable URLs must be the Jellyfin MPEG-TS proxy (`/LiveTv/LiveStreamFiles/`
 * or `/LiveStreams/`), never the raw provider ingest host.
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
            "tvchannel", "program", "livetvprogram", "channel" -> true
            else -> false
        }
    }

    fun isLiveSource(source: String): Boolean {
        return jsonBooleanField(source, "IsInfiniteStream") == true ||
            jsonBooleanField(source, "RequiresOpening") == true ||
            !jsonStringField(source, "LiveStreamId").isNullOrBlank() ||
            PlayUrl.isLiveProxy(jsonStringField(source, "Path").orEmpty())
    }

    fun isLive(payload: String, source: String?): Boolean {
        return isLivePayload(payload) || (source != null && isLiveSource(source))
    }

    fun isTunerChannelId(id: String?): Boolean {
        val value = id?.trim().orEmpty()
        if (value.isEmpty()) {
            return false
        }
        return value.startsWith("m3u_", ignoreCase = true) ||
            value.startsWith("hdhr_", ignoreCase = true)
    }

    fun tunerChannelId(payload: String): String? {
        val item = jsonArrayObjects(payload, "items").firstOrNull()
        val candidates = listOfNotNull(
            item?.let { jsonStringField(it, "ExternalId") ?: jsonStringField(it, "externalId") },
            item?.let { jsonStringField(it, "ChannelId") ?: jsonStringField(it, "channelId") },
            jsonStringField(payload, "channelId"),
            jsonStringField(payload, "openToken"),
            itemIdFrom(payload),
        )
        return candidates.firstOrNull { isTunerChannelId(it) }
    }

    fun isUsableLiveSource(source: String): Boolean {
        val protocol = jsonStringField(source, "Protocol")
        val path = jsonStringField(source, "Path")
        if (protocol.equals("File", ignoreCase = true) &&
            (path.isNullOrBlank() || !PlayUrl.isAbsoluteHttp(path))
        ) {
            return false
        }
        return isLiveSource(source) ||
            !jsonStringField(source, "OpenToken").isNullOrBlank() ||
            !jsonStringField(source, "DirectStreamUrl").isNullOrBlank() ||
            !jsonStringField(source, "TranscodingUrl").isNullOrBlank()
    }

    fun mimeType(container: String?, url: String): String? {
        val kind = container?.lowercase().orEmpty()
        val path = url.lowercase()
        return when {
            kind == "hls" || path.contains(".m3u8") -> "application/x-mpegURL"
            kind == "mpegts" || kind == "ts" || path.contains("mpegts") ||
                path.contains("stream.ts") || path.contains("/livestreams/") ||
                path.contains("/livestreamfiles/") -> "video/mp2t"
            else -> null
        }
    }

    private fun itemIdFrom(payload: String): String? {
        return jsonArrayObjects(payload, "items").firstOrNull()?.let {
            jsonStringField(it, "Id") ?: jsonStringField(it, "id")
        } ?: jsonStringField(payload, "itemId")
    }
}
