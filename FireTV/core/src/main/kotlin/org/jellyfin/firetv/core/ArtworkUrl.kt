package org.jellyfin.firetv.core

import java.net.URI

/**
 * Caps poster/backdrop requests so a Fire TV Stick does not download 4K artwork
 * for every card in the TV layout.
 */
object ArtworkUrl {
    const val POSTER_MAX_PX: Int = 720
    const val BACKDROP_MAX_PX: Int = 1280
    const val LOGO_MAX_PX: Int = 480
    const val QUALITY: Int = 70

    @Deprecated("Use POSTER_MAX_PX", ReplaceWith("POSTER_MAX_PX"))
    const val MAX_EDGE_PX: Int = POSTER_MAX_PX

    fun maxEdge(url: String): Int {
        val path = runCatching { URI(url).path }.getOrNull()?.lowercase() ?: return POSTER_MAX_PX
        return when {
            path.contains("/images/backdrop") ||
                path.contains("/images/thumb") ||
                path.contains("/images/banner") -> BACKDROP_MAX_PX
            path.contains("/images/logo") || path.contains("/images/art") -> LOGO_MAX_PX
            else -> POSTER_MAX_PX
        }
    }

    fun shouldDownscale(url: String): Boolean {
        val path = runCatching { URI(url).path }.getOrNull() ?: return false
        if (!ResourceKind.isArtwork(path)) {
            return false
        }
        val requested = requestedEdge(url)
        return requested == null || requested > maxEdge(url)
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
        kept += "maxWidth" to maxEdge(url).toString()
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
