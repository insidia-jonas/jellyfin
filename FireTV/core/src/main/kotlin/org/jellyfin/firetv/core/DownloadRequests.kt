package org.jellyfin.firetv.core

/**
 * Parses jellyfin-web `NativeShell.downloadFile` / `downloadFiles` payloads.
 */
object DownloadRequests {
    data class File(
        val url: String,
        val filename: String?,
        val title: String?,
        val itemId: String?,
    )

    fun parse(json: String): List<File> {
        val trimmed = json.trim()
        if (trimmed.isEmpty()) {
            return emptyList()
        }
        val blobs = when {
            trimmed.startsWith("[") -> jsonArrayObjects("{\"files\":$trimmed}", "files")
            else -> listOf(trimmed)
        }
        return blobs.mapNotNull { parseOne(it) }
    }

    fun safeFilename(raw: String?, fallback: String = "jellyfin-download"): String {
        val leaf = (raw ?: "")
            .substringAfterLast('/')
            .substringAfterLast('\\')
            .trim()
        val cleaned = leaf.replace(UNSAFE_CHARS, "_").trim('.', ' ', '_')
        return cleaned.take(120).ifBlank { fallback }
    }

    private fun parseOne(json: String): File? {
        val url = jsonStringField(json, "url") ?: jsonStringField(json, "shareUrl") ?: return null
        if (!url.startsWith("http://", ignoreCase = true) && !url.startsWith("https://", ignoreCase = true)) {
            return null
        }
        return File(
            url = url,
            filename = jsonStringField(json, "filename") ?: jsonStringField(json, "fileName"),
            title = jsonStringField(json, "title") ?: jsonStringField(json, "itemName"),
            itemId = jsonStringField(json, "itemId") ?: jsonStringField(json, "Id"),
        )
    }

    private val UNSAFE_CHARS = Regex("""[^\w.\- ()\[\]]+""")
}
