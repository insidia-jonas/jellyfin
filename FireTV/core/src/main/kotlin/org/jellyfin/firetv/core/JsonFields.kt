package org.jellyfin.firetv.core

/** Keeps only root fields, so a queued item's flags cannot describe the playing item. */
internal fun jsonShallowObject(json: String): String = buildString {
    var depth = 0
    var quoted = false
    var escaped = false
    for (ch in json) {
        if (quoted) {
            if (depth <= 1) append(ch)
            if (escaped) escaped = false
            else if (ch == '\\') escaped = true
            else if (ch == '"') quoted = false
        } else when (ch) {
            '"' -> { quoted = true; if (depth <= 1) append(ch) }
            '{', '[' -> { if (depth == 0) append(ch) else if (depth == 1) append("null"); depth++ }
            '}', ']' -> { depth--; if (depth == 0) append(ch) }
            else -> if (depth <= 1) append(ch)
        }
    }
}

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

internal fun jsonObjectField(json: String, key: String): String? {
    val match = Regex("\"${Regex.escape(key)}\"\\s*:\\s*\\{").find(json) ?: return null
    val open = match.range.last
    val close = matchingBrace(json, open) ?: return null
    return json.substring(open, close + 1)
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
    // System.Text.Json escapes URL query separators as \u0026. Decode one
    // escape at a time so literal backslashes are not decoded a second time.
    return buildString {
        var i = 0
        while (i < raw.length) {
            val value = raw[i++]
            if (value != '\\' || i >= raw.length) {
                append(value)
                continue
            }
            when (val escaped = raw[i++]) {
                '"', '\\', '/' -> append(escaped)
                'b' -> append('\b')
                'f' -> append('\u000C')
                'n' -> append('\n')
                'r' -> append('\r')
                't' -> append('\t')
                'u' -> {
                    require(i + 4 <= raw.length) { "Incomplete JSON unicode escape" }
                    append(raw.substring(i, i + 4).toInt(16).toChar())
                    i += 4
                }
                else -> error("Invalid JSON escape")
            }
        }
    }
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
