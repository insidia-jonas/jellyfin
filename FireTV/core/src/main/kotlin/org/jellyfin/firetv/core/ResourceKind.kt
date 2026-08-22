package org.jellyfin.firetv.core

/**
 * Classifies Jellyfin URLs so the WebView never intercepts media as HTML.
 * Intercepting `/Videos/.../stream` as a document is a common ANR/hang on Fire TV.
 */
object ResourceKind {
    fun isNativeBridge(path: String): Boolean {
        return path.contains("/native/", ignoreCase = true)
    }

    fun isMedia(path: String): Boolean {
        val p = path.lowercase()
        if (p.contains("/videos/") || p.contains("/audio/") || p.contains("/livestreams/")) {
            return true
        }
        if (p.contains("/items/") && (p.contains("/download") || p.contains("/file"))) {
            return true
        }
        return MEDIA_SUFFIXES.any { p.endsWith(it) } || p.contains("master.m3u8") || p.contains("/stream")
    }

    private val MEDIA_SUFFIXES = listOf(
        ".m3u8", ".mp4", ".m4v", ".mkv", ".webm", ".mp3", ".aac", ".flac",
        ".ts", ".m4s", ".mpd", ".ism", ".wav", ".ogg",
    )
}
