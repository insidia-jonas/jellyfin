package org.jellyfin.firetv.core

data class MediaSourceUrls(
    val transcodingUrl: String? = null,
    val directStreamUrl: String? = null,
    val path: String? = null,
    val supportsDirectPlay: Boolean = false,
    val supportsDirectStream: Boolean = false,
)

/**
 * Picks a playable HTTP URL from a Jellyfin MediaSource, preferring direct stream
 * (ExoPlayer can play MKV on Fire TV) and falling back to HLS transcoding.
 */
object PlayUrl {
    fun resolve(serverBase: String, source: MediaSourceUrls): String? {
        val base = serverBase.trimEnd('/')
        val path = source.path
        // Direct play of a remote HTTP(S) file. Never treat a server filesystem
        // path like `/mnt/media/movie.mkv` as a URL — ExoPlayer would hang on it.
        if (source.supportsDirectPlay && path != null && isAbsoluteHttp(path)) {
            return path
        }
        if (source.supportsDirectStream && !source.directStreamUrl.isNullOrBlank()) {
            return absolutize(base, source.directStreamUrl!!)
        }
        if (!source.transcodingUrl.isNullOrBlank()) {
            return absolutize(base, source.transcodingUrl!!)
        }
        if (!path.isNullOrBlank() && isAbsoluteHttp(path)) {
            return path
        }
        return null
    }

    fun absolutize(serverBase: String, url: String): String {
        val trimmed = url.trim()
        if (isAbsoluteHttp(trimmed)) {
            return trimmed
        }
        val base = serverBase.trimEnd('/')
        return if (trimmed.startsWith("/")) base + trimmed else "$base/$trimmed"
    }

    private fun isAbsoluteHttp(url: String): Boolean {
        return url.startsWith("http://", ignoreCase = true) || url.startsWith("https://", ignoreCase = true)
    }
}
