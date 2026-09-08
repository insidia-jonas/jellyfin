package org.jellyfin.firetv.core

/**
 * Ranks library search hits the way a TV client should: exact title first,
 * then prefix, then token overlap, with year / rating / preferred-library boosts.
 *
 * jellyfin-web skips in-library search for mixed folders (e.g. Treasure Maps).
 * The Fire TV shell uses this scoring when it merges Items API results.
 */
object SmartSearch {
    data class Candidate(
        val id: String,
        val name: String,
        val type: String = "",
        val year: Int? = null,
        val communityRating: Double? = null,
        val parentId: String? = null,
        val officialRating: String? = null,
    )

    data class ParsedQuery(
        val raw: String,
        val normalized: String,
        val tokens: List<String>,
        val year: Int?,
    )

    fun parseQuery(raw: String): ParsedQuery {
        val trimmed = raw.trim()
        val year = YEAR_IN_TEXT.find(trimmed)?.value?.toIntOrNull()
        val withoutYear = trimmed.replace(YEAR_IN_TEXT, " ")
        val normalized = normalize(withoutYear)
        val tokens = normalized.split(' ').filter { it.length >= 2 }
        return ParsedQuery(trimmed, normalized, tokens, year)
    }

    fun variants(raw: String): List<String> {
        val parsed = parseQuery(raw)
        val out = linkedSetOf<String>()
        if (parsed.raw.isNotBlank()) out.add(parsed.raw)
        if (parsed.normalized.isNotBlank()) out.add(parsed.normalized)
        val folded = foldGerman(parsed.normalized)
        if (folded.isNotBlank()) out.add(folded)
        val expanded = expandGerman(parsed.normalized)
        if (expanded.isNotBlank()) out.add(expanded)
        parsed.year?.let { year ->
            out.add("${parsed.normalized} $year".trim())
        }
        return out.filter { it.length >= 2 }.distinct()
    }

    fun score(candidate: Candidate, query: ParsedQuery, preferredParentId: String? = null): Float {
        val name = normalize(candidate.name)
        if (name.isEmpty() || query.normalized.isEmpty()) {
            return 0f
        }
        var points = when {
            name == query.normalized -> 100f
            name.startsWith(query.normalized) -> 82f
            name.contains(query.normalized) -> 64f
            query.tokens.isNotEmpty() && query.tokens.all { name.contains(it) } -> 58f
            query.tokens.count { name.contains(it) } >= 1 -> 28f + (12f * query.tokens.count { name.contains(it) })
            else -> 8f
        }
        if (query.year != null && candidate.year == query.year) {
            points += 16f
        }
        if (!preferredParentId.isNullOrBlank() && candidate.parentId == preferredParentId) {
            points += 22f
        }
        when (candidate.type.lowercase()) {
            "movie", "series", "boxset" -> points += 10f
            "tvchannel", "livetvprogram", "program" -> points += 8f
            "folder", "collectionfolder" -> points -= 6f
            "episode", "season" -> points -= 4f
        }
        candidate.communityRating?.let { rating ->
            if (rating > 0) {
                points += (rating.coerceIn(0.0, 10.0) / 10.0 * 10.0).toFloat()
            }
        }
        return points
    }

    fun rank(candidates: List<Candidate>, rawQuery: String, preferredParentId: String? = null): List<Candidate> {
        val query = parseQuery(rawQuery)
        return candidates
            .distinctBy { it.id }
            .map { it to score(it, query, preferredParentId) }
            .sortedByDescending { it.second }
            .map { it.first }
    }

    fun normalize(value: String): String {
        var text = value.lowercase().trim()
        text = foldGerman(text)
        ARTICLES.forEach { article ->
            if (text.startsWith("$article ")) {
                text = text.removePrefix("$article ")
            }
        }
        return text.replace(NOT_ALPHANUMERIC, " ").replace(WHITESPACE, " ").trim()
    }

    fun extractYear(value: String): Int? = YEAR_IN_TEXT.find(value)?.value?.toIntOrNull()

    fun looksFailed(text: String): Boolean {
        val sample = text.lowercase()
        return FAILED_MARKERS.any { sample.contains(it) }
    }

    private fun foldGerman(value: String): String {
        return value
            .replace("ä", "ae")
            .replace("ö", "oe")
            .replace("ü", "ue")
            .replace("ß", "ss")
    }

    private fun expandGerman(value: String): String {
        return value
            .replace("ae", "ä")
            .replace("oe", "ö")
            .replace("ue", "ü")
            .replace("ss", "ß")
    }

    private val YEAR_IN_TEXT = Regex("""\b((?:19|20)\d{2})\b""")
    private val NOT_ALPHANUMERIC = Regex("[^a-z0-9äöüß]+")
    private val WHITESPACE = Regex("\\s+")
    private val ARTICLES = listOf("the", "der", "die", "das", "ein", "eine", "a", "an", "le", "la", "les")
    private val FAILED_MARKERS = listOf(
        "failed to retrieve",
        "failed to load",
        "request failed",
        "error retrieving",
        "section failed",
        "failed categor",
        "fehlgeschlagen",
        "konnte nicht geladen",
        "konnte nicht abgerufen",
        "fehler beim laden",
    )
}
