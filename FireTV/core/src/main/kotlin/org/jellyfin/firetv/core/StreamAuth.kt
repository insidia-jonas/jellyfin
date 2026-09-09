package org.jellyfin.firetv.core

import java.net.URLEncoder

/**
 * Jellyfin media URLs often rely on the WebView cookie jar. ExoPlayer and
 * DownloadManager do not share that jar, so the access token must travel with
 * the request as `api_key` (and as HTTP headers on the player).
 */
object StreamAuth {
    fun withAccessToken(url: String, token: String): String {
        if (token.isBlank()) {
            return url
        }
        if (url.contains("api_key=", ignoreCase = true)) {
            return url
        }
        val encoded = URLEncoder.encode(token, Charsets.UTF_8.name())
        val separator = if (url.contains('?')) '&' else '?'
        return "$url${separator}api_key=$encoded"
    }
}
