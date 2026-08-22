package org.jellyfin.firetv.core

internal fun jsonStringField(json: String, key: String): String? {
    val pattern = Regex("\"${Regex.escape(key)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
    val raw = pattern.find(json)?.groupValues?.getOrNull(1) ?: return null
    return raw.replace("\\\"", "\"")
        .replace("\\\\", "\\")
        .replace("\\/", "/")
        .replace("\\n", "\n")
        .replace("\\t", "\t")
}
