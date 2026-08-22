package org.jellyfin.firetv.core

internal fun jsonStringField(json: String, key: String): String? {
    val pattern = Regex("\"${Regex.escape(key)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
    val raw = pattern.find(json)?.groupValues?.getOrNull(1) ?: return null
    return unescapeJsonString(raw)
}

internal fun jsonBooleanField(json: String, key: String): Boolean? {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*(true|false)").find(json) ?: return null
    return match.groupValues[1] == "true"
}

internal fun jsonLongField(json: String, key: String): Long? {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*(-?\\d+)").find(json) ?: return null
    return match.groupValues[1].toLongOrNull()
}

internal fun jsonDoubleField(json: String, key: String): Double? {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)").find(json) ?: return null
    return match.groupValues[1].toDoubleOrNull()
}

internal fun jsonRootArrayObjects(json: String): List<String> {
    val trimmed = json.trim()
    if (!trimmed.startsWith("[")) {
        return emptyList()
    }
    return jsonArrayObjects("{\"items\":$trimmed}", "items")
}

internal fun jsonStringArray(json: String, key: String): List<String> {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*\\[").find(json) ?: return emptyList()
    val from = match.range.last + 1
    val end = findMatchingBracket(json, match.range.last) ?: return emptyList()
    val body = json.substring(from, end)
    return Regex("\"((?:\\\\.|[^\"\\\\])*)\"")
        .findAll(body)
        .map { unescapeJsonString(it.groupValues[1]) }
        .toList()
}

internal fun jsonArrayObjects(json: String, key: String): List<String> {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*\\[").find(json) ?: return emptyList()
    val end = findMatchingBracket(json, match.range.last) ?: return emptyList()
    val body = json.substring(match.range.last + 1, end)
    val objects = mutableListOf<String>()
    var i = 0
    while (i < body.length) {
        when (body[i]) {
            '{' -> {
                val close = matchingBrace(body, i) ?: break
                objects += body.substring(i, close + 1)
                i = close + 1
            }
            else -> i++
        }
    }
    return objects
}

internal fun jsonEscape(value: String): String {
    val escaped = buildString(value.length + 8) {
        for (ch in value) {
            when (ch) {
                '\\' -> append("\\\\")
                '"' -> append("\\\"")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                '\t' -> append("\\t")
                else -> append(ch)
            }
        }
    }
    return "\"$escaped\""
}

private fun unescapeJsonString(raw: String): String {
    return raw.replace("\\\"", "\"")
        .replace("\\\\", "\\")
        .replace("\\/", "/")
        .replace("\\n", "\n")
        .replace("\\t", "\t")
}

private fun findMatchingBracket(source: String, openIndex: Int): Int? {
    var depth = 0
    var inString = false
    var escape = false
    for (i in openIndex until source.length) {
        val ch = source[i]
        if (inString) {
            if (escape) {
                escape = false
            } else if (ch == '\\') {
                escape = true
            } else if (ch == '"') {
                inString = false
            }
            continue
        }
        when (ch) {
            '"' -> inString = true
            '[' -> depth++
            ']' -> {
                depth--
                if (depth == 0) {
                    return i
                }
            }
        }
    }
    return null
}

internal fun matchingBrace(source: String, start: Int): Int? {
    var depth = 0
    var inString = false
    var escape = false
    for (i in start until source.length) {
        val ch = source[i]
        if (inString) {
            if (escape) {
                escape = false
            } else if (ch == '\\') {
                escape = true
            } else if (ch == '"') {
                inString = false
            }
            continue
        }
        when (ch) {
            '"' -> inString = true
            '{' -> depth++
            '}' -> {
                depth--
                if (depth == 0) {
                    return i
                }
            }
        }
    }
    return null
}
