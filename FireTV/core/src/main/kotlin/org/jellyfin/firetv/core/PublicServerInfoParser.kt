package org.jellyfin.firetv.core

/**
 * Parses `/System/Info/Public` so the connect screen can verify a Jellyfin server
 * without using a session token or the full client SDK.
 */
object PublicServerInfoParser {
    fun parse(json: String): PublicServerInfo? {
        if (json.isBlank() || !json.contains("{")) {
            return null
        }
        return PublicServerInfo(
            id = jsonStringField(json, "Id") ?: jsonStringField(json, "id"),
            serverName = jsonStringField(json, "ServerName") ?: jsonStringField(json, "serverName"),
            version = jsonStringField(json, "Version") ?: jsonStringField(json, "version"),
            productName = jsonStringField(json, "ProductName") ?: jsonStringField(json, "productName"),
        ).takeIf { it.isJellyfin }
    }
}

data class PublicServerInfo(
    val id: String?,
    val serverName: String?,
    val version: String?,
    val productName: String?,
) {
    val isJellyfin: Boolean
        get() = !id.isNullOrBlank() ||
            !serverName.isNullOrBlank() ||
            productName?.contains("Jellyfin", ignoreCase = true) == true
}
