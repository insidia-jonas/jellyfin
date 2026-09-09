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

    data class Parsed(
        val files: List<File>,
        val accessToken: String?,
    )

    fun parse(json: String): List<File> = parseDetailed(json).files

    fun parseDetailed(json: String): Parsed {
        val trimmed = json.trim()
        if (trimmed.isEmpty()) {
            return Parsed(emptyList(), null)
        }
        val token = jsonStringField(trimmed, "accessToken")
            ?: jsonStringField(trimmed, "api_key")
        val blobs = when {
            trimmed.startsWith("[") -> jsonArrayObjects("{\"files\":$trimmed}", "files")
            jsonArrayObjects(trimmed, "files").isNotEmpty() -> jsonArrayObjects(trimmed, "files")
            jsonArrayObjects(trimmed, "items").isNotEmpty() -> jsonArrayObjects(trimmed, "items")
            else -> listOf(trimmed)
        }
        return Parsed(blobs.mapNotNull { parseOne(it) }, token)
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
