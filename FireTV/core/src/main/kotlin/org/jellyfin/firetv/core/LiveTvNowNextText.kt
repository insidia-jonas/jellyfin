package org.jellyfin.firetv.core

/**
 * Reads the Live TV library-channel tile contract from server PR #1:
 * [Name] = "Sender  ·  Jetzt-Titel", [OriginalTitle] = Sender,
 * [Overview] = "Jetzt: … (HH:mm–HH:mm)\nDanach: …\n\nPlot".
 */
object LiveTvNowNextText {
    data class Guide(
        val channelName: String,
        val nowTitle: String? = null,
        val nowRange: String? = null,
        val nextTitle: String? = null,
        val nextRange: String? = null,
        val plot: String? = null,
        val isGroupFolder: Boolean = false,
        val groupCount: Int? = null,
    ) {
        fun nowLine(german: Boolean = true): String? {
            val title = nowTitle?.takeIf { it.isNotBlank() } ?: return folderLine()
            val prefix = if (german) "Jetzt: " else "Now: "
            return prefix + title + rangeSuffix(nowRange)
        }

        fun nextLine(german: Boolean = true): String? {
            val title = nextTitle?.takeIf { it.isNotBlank() } ?: return null
            val prefix = if (german) "Danach: " else "Next: "
            return prefix + title + rangeSuffix(nextRange)
        }

        fun folderLine(): String? {
            val count = groupCount ?: return null
            return if (count == 1) "1 Sender" else "$count Sender"
        }
    }

    fun parse(name: String?, originalTitle: String? = null, overview: String? = null): Guide {
        val rawName = name?.trim().orEmpty()
        val original = originalTitle?.trim()?.takeIf { it.isNotBlank() }
        val split = splitCardName(rawName)
        val overviewLines = overview?.replace("\r\n", "\n")?.trim().orEmpty()
        val nowFromOverview = labeledLine(overviewLines, NOW_LABELS)
        val nextFromOverview = labeledLine(overviewLines, NEXT_LABELS)
        val senderCount = SENDER_COUNT.find(overviewLines)?.groupValues?.get(1)?.toIntOrNull()
            ?: split.second?.toIntOrNull()?.takeIf { split.second?.all(Char::isDigit) == true && overviewLines.contains("Sender") }
        val isFolder = senderCount != null &&
            nowFromOverview == null &&
            (overviewLines.isBlank() || overviewLines.contains("Sender"))
        val channel = when {
            !isFolder && !original.isNullOrBlank() -> original
            split.first.isNotBlank() -> split.first
            else -> rawName
        }
        val nowTitle = if (isFolder) {
            null
        } else {
            nowFromOverview?.title ?: split.second?.takeIf { it.any { ch -> !ch.isDigit() } }
        }
        return Guide(
            channelName = channel.ifBlank { rawName },
            nowTitle = nowTitle?.trim()?.takeIf { it.isNotBlank() },
            nowRange = nowFromOverview?.range,
            nextTitle = if (isFolder) null else nextFromOverview?.title,
            nextRange = nextFromOverview?.range,
            plot = if (isFolder) null else plotFromOverview(overviewLines),
            isGroupFolder = isFolder,
            groupCount = senderCount,
        )
    }

    fun channelTitle(name: String?, originalTitle: String? = null, overview: String? = null): String {
        return parse(name, originalTitle, overview).channelName.ifBlank { name?.trim().orEmpty() }
    }

    fun looksLikeLibraryTile(name: String?, originalTitle: String? = null, overview: String? = null): Boolean {
        val text = listOfNotNull(name, originalTitle, overview).joinToString("\n")
        if (NOW_LABELS.any { text.contains(it, ignoreCase = true) } ||
            NEXT_LABELS.any { text.contains(it, ignoreCase = true) }
        ) {
            return true
        }
        if (SENDER_COUNT.containsMatchIn(text)) {
            return true
        }
        return name?.contains("  ·  ") == true && !originalTitle.isNullOrBlank()
    }

    private data class Labeled(val title: String, val range: String?)

    private fun splitCardName(name: String): Pair<String, String?> {
        val index = name.indexOf("  ·  ")
        if (index <= 0) {
            return name to null
        }
        return name.substring(0, index).trim() to name.substring(index + 5).trim().takeIf { it.isNotBlank() }
    }

    private fun labeledLine(overview: String, labels: List<String>): Labeled? {
        overview.lineSequence().forEach { raw ->
            val line = raw.trim()
            val label = labels.firstOrNull { line.startsWith(it, ignoreCase = true) } ?: return@forEach
            val rest = line.substring(label.length).trim()
            if (rest.isBlank()) {
                return@forEach
            }
            val rangeMatch = TRAILING_RANGE.find(rest)
            return if (rangeMatch != null) {
                Labeled(rest.substring(0, rangeMatch.range.first).trim(), rangeMatch.groupValues[1])
            } else {
                Labeled(rest, null)
            }
        }
        return null
    }

    private fun plotFromOverview(overview: String): String? {
        val leftover = overview.lineSequence()
            .map { it.trim() }
            .filter { line ->
                line.isNotBlank() &&
                    NOW_LABELS.none { line.startsWith(it, ignoreCase = true) } &&
                    NEXT_LABELS.none { line.startsWith(it, ignoreCase = true) }
            }
            .joinToString("\n")
            .trim()
        return leftover.takeIf { it.isNotBlank() && !it.contains("Sender") }
    }

    private fun rangeSuffix(range: String?): String {
        return if (range.isNullOrBlank()) "" else " ($range)"
    }

    private val NOW_LABELS = listOf("Jetzt:", "Now:")
    private val NEXT_LABELS = listOf("Danach:", "Next:")
    private val TRAILING_RANGE = Regex("""\(([^)]+)\)$""")
    private val SENDER_COUNT = Regex("""\b(\d+)\s+Sender\b""", RegexOption.IGNORE_CASE)
}
