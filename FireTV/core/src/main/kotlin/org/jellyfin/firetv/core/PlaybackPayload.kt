package org.jellyfin.firetv.core

/**
 * Reads the JSON blob jellyfin-web / ExoPlayerPlugin posts to NativePlayer.loadPlayer.
 *
 * Official Android sanitizes `items` down to `ids`. This client accepts both so
 * older and newer plugin payloads keep working.
 */
object PlaybackPayload {
    fun itemId(json: String): String? {
        val fromItem = jsonArrayObjects(json, "items").firstOrNull()?.let {
            jsonStringField(it, "Id") ?: jsonStringField(it, "id")
        }
        if (!fromItem.isNullOrBlank()) {
            return fromItem
        }
        return jsonStringArray(json, "ids").firstOrNull()?.takeIf { it.isNotBlank() }
            ?: jsonStringField(json, "itemId")
            ?: jsonStringField(json, "Id")
    }

    fun itemName(json: String): String {
        val name = jsonArrayObjects(json, "items").firstOrNull()?.let {
            jsonStringField(it, "Name") ?: jsonStringField(it, "name") ?: jsonStringField(it, "Path")
        }
        return name?.takeIf { it.isNotBlank() } ?: "Jellyfin"
    }

    fun itemOriginalTitle(json: String): String? {
        return jsonArrayObjects(json, "items").firstOrNull()?.let {
            jsonStringField(it, "OriginalTitle") ?: jsonStringField(it, "originalTitle")
        }?.takeIf { it.isNotBlank() }
    }

    fun itemOverview(json: String): String? {
        return jsonArrayObjects(json, "items").firstOrNull()?.let {
            jsonStringField(it, "Overview") ?: jsonStringField(it, "overview")
        }?.takeIf { it.isNotBlank() }
    }

    fun serverAddress(json: String): String? {
        return jsonStringField(json, "serverAddress")?.trim()?.trimEnd('/')
    }

    fun accessToken(json: String): String = jsonStringField(json, "accessToken").orEmpty()

    fun userId(json: String): String = jsonStringField(json, "userId").orEmpty()

    fun itemType(json: String): String? = LivePlayback.itemType(json)

    fun mediaType(json: String): String? {
        val fromItem = jsonArrayObjects(json, "items").firstOrNull()?.let {
            jsonStringField(it, "MediaType") ?: jsonStringField(it, "mediaType")
        }
        return fromItem?.ifBlank { null } ?: jsonStringField(json, "mediaType")
    }

    fun isAudio(json: String): Boolean = mediaType(json)?.equals("Audio", ignoreCase = true) == true

    fun itemIds(json: String): List<String> {
        val fromItems = jsonArrayObjects(json, "items").mapNotNull { item ->
            (jsonStringField(item, "Id") ?: jsonStringField(item, "id"))?.takeIf { it.isNotBlank() }
        }
        if (fromItems.isNotEmpty()) {
            return fromItems.distinct()
        }
        return jsonStringArray(json, "ids").filter { it.isNotBlank() }.distinct()
    }

    fun nextItemId(json: String, currentId: String): String? {
        val ids = itemIds(json)
        val index = ids.indexOf(currentId)
        if (index < 0 || index >= ids.lastIndex) {
            return null
        }
        return ids[index + 1]
    }

    fun previousItemId(json: String, currentId: String): String? {
        val ids = itemIds(json)
        val index = ids.indexOf(currentId)
        if (index <= 0) {
            return null
        }
        return ids[index - 1]
    }

    fun retarget(json: String, itemId: String): String {
        val match = jsonArrayObjects(json, "items").firstOrNull { item ->
            (jsonStringField(item, "Id") ?: jsonStringField(item, "id")) == itemId
        }
        val name = match?.let { jsonStringField(it, "Name") ?: jsonStringField(it, "name") } ?: itemId
        val original = match?.let { jsonStringField(it, "OriginalTitle") ?: jsonStringField(it, "originalTitle") }
        val overview = match?.let { jsonStringField(it, "Overview") ?: jsonStringField(it, "overview") }
        val type = match?.let { jsonStringField(it, "Type") ?: jsonStringField(it, "type") }
        val media = match?.let { jsonStringField(it, "MediaType") ?: jsonStringField(it, "mediaType") }
        val live = match?.let { jsonBooleanField(it, "IsLiveStream") } == true
        return buildString {
            append('{')
            append("\"ids\":[").append(jsonEscape(itemId)).append("],")
            append("\"items\":[{")
            append("\"Id\":").append(jsonEscape(itemId)).append(',')
            append("\"Name\":").append(jsonEscape(name))
            if (!original.isNullOrBlank()) {
                append(",\"OriginalTitle\":").append(jsonEscape(original))
            }
            if (!overview.isNullOrBlank()) {
                append(",\"Overview\":").append(jsonEscape(overview))
            }
            if (!type.isNullOrBlank()) {
                append(",\"Type\":").append(jsonEscape(type))
            }
            if (!media.isNullOrBlank()) {
                append(",\"MediaType\":").append(jsonEscape(media))
            }
            if (live) {
                append(",\"IsLiveStream\":true")
            }
            append("}],")
            append("\"serverAddress\":").append(jsonEscape(serverAddress(json).orEmpty())).append(',')
            append("\"accessToken\":").append(jsonEscape(accessToken(json))).append(',')
            append("\"userId\":").append(jsonEscape(userId(json))).append(',')
            append("\"deviceId\":").append(jsonEscape(jsonStringField(json, "deviceId").orEmpty())).append(',')
            append("\"deviceName\":").append(jsonEscape(jsonStringField(json, "deviceName") ?: "Fire TV")).append(',')
            append("\"appName\":").append(jsonEscape(jsonStringField(json, "appName") ?: FireTvClient.APP_NAME)).append(',')
            append("\"appVersion\":").append(jsonEscape(jsonStringField(json, "appVersion") ?: FireTvClient.APP_VERSION)).append(',')
            append("\"startPositionTicks\":0")
            append('}')
        }
    }
}
