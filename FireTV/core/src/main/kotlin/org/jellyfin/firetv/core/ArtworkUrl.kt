package org.jellyfin.firetv.core

import java.net.URI

/**
 * Caps poster/backdrop requests so a Fire TV Stick does not download 4K artwork
 * for every card in the TV layout.
 */
object ArtworkUrl {
    const val MAX_EDGE_PX: Int = 720
    const val QUALITY: Int = 70

    fun shouldDownscale(url: String): Boolean {
        val path = runCatching { URI(url).path }.getOrNull() ?: return false
        if (!ResourceKind.isArtwork(path)) {
            return false
        }
        val requested = requestedEdge(url)
        return requested == null || requested > MAX_EDGE_PX
    }

    fun downscale(url: String): String {
        val uri = URI(url)
        val kept = mutableListOf<Pair<String, String>>()
        val query = uri.rawQuery.orEmpty()
        if (query.isNotEmpty()) {
            for (part in query.split('&')) {
                if (part.isBlank()) continue
                val name = part.substringBefore('=').lowercase()
                if (name == "maxwidth" || name == "maxheight" || name == "fillwidth" || name == "fillheight" || name == "quality") {
                    continue
                }
                val value = part.substringAfter('=', missingDelimiterValue = "")
                kept += name to value
            }
        }
        kept += "maxWidth" to MAX_EDGE_PX.toString()
        kept += "quality" to QUALITY.toString()
        val newQuery = kept.joinToString("&") { (k, v) -> if (v.isEmpty()) k else "$k=$v" }
        val base = "${uri.scheme}://${uri.authority}${uri.path}"
        val fragment = uri.rawFragment?.let { "#$it" }.orEmpty()
        return "$base?$newQuery$fragment"
    }

    private fun requestedEdge(url: String): Int? {
        val query = URI(url).rawQuery ?: return null
        val values = query.split('&').mapNotNull { part ->
            val name = part.substringBefore('=').lowercase()
            val value = part.substringAfter('=', missingDelimiterValue = "")
            if (name == "maxwidth" || name == "fillwidth" || name == "maxheight" || name == "fillheight") {
                value.toIntOrNull()
            } else {
                null
            }
        }
        return values.maxOrNull()
    }
}
