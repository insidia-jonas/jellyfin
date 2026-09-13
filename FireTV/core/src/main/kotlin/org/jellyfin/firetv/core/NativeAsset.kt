package org.jellyfin.firetv.core

/**
 * MIME types for native bridge assets served into the WebView.
 *
 * ES module scripts such as ExoPlayerPlugin.js require a JavaScript MIME type.
 * Some Fire OS WebViews reject application/javascript and demand text/javascript.
 */
object NativeAsset {
    fun mimeType(fileName: String): String {
        val name = fileName.lowercase()
        return when {
            name.endsWith(".js") -> "text/javascript"
            name.endsWith(".css") -> "text/css"
            name.endsWith(".json") -> "application/json"
            name.endsWith(".svg") -> "image/svg+xml"
            else -> "application/octet-stream"
        }
    }

    fun responseHeaders(fileName: String): Map<String, String> {
        val mime = mimeType(fileName)
        return mapOf(
            "Content-Type" to "$mime; charset=utf-8",
            "Cache-Control" to "no-cache",
            "Access-Control-Allow-Origin" to "*",
        )
    }
}
