package org.jellyfin.firetv.core

/**
 * Parses the JSON payload returned by Jellyfin UDP discovery (port 7359).
 *
 * Accepts both PascalCase (`Address`) and camelCase (`address`) property names.
 */
object DiscoveryResponseParser {
    fun parse(json: String): DiscoveredServer? {
        val address = jsonStringField(json, "Address") ?: jsonStringField(json, "address")
        val id = jsonStringField(json, "Id") ?: jsonStringField(json, "id")
        val name = jsonStringField(json, "Name") ?: jsonStringField(json, "name")
        if (address.isNullOrBlank() || id.isNullOrBlank() || name.isNullOrBlank()) {
            return null
        }
        val normalized = ServerUrl.normalize(address) ?: return null
        return DiscoveredServer(address = normalized, id = id, name = name)
    }
}

data class DiscoveredServer(
    val address: String,
    val id: String,
    val name: String,
)
