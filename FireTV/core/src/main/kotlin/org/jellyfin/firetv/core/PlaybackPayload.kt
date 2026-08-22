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

    fun serverAddress(json: String): String? {
        return jsonStringField(json, "serverAddress")?.trim()?.trimEnd('/')
    }

    fun accessToken(json: String): String = jsonStringField(json, "accessToken").orEmpty()

    fun userId(json: String): String = jsonStringField(json, "userId").orEmpty()
}
