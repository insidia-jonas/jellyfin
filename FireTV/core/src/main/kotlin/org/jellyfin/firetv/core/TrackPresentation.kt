package org.jellyfin.firetv.core

import java.util.Locale

/** Human-readable labels without treating release names as languages or formats. */
object TrackPresentation {
    private val aliases = mapOf("ger" to "de", "deu" to "de", "eng" to "en", "fre" to "fr", "fra" to "fr",
        "spa" to "es", "ita" to "it", "dut" to "nl", "nld" to "nl", "por" to "pt", "pol" to "pl",
        "jpn" to "ja", "zho" to "zh", "chi" to "zh", "rus" to "ru", "ara" to "ar", "tur" to "tr")

    fun language(track: MediaTrack, locale: Locale): String? {
        val code = track.language?.trim()?.lowercase(Locale.ROOT)?.takeUnless { it in setOf("", "und", "unk") }
            ?: mapOf("Deutsch" to "de", "German" to "de", "English" to "en", "Englisch" to "en",
                "Français" to "fr", "French" to "fr", "Español" to "es", "Spanish" to "es", "Italiano" to "it")
                .entries.firstOrNull { Regex("(?i)^" + it.key + "(?:$|\\s|[·–])").containsMatchIn(track.displayTitle.trim()) }?.value
            ?: return null
        val normalized = aliases[code] ?: code
        val language = Locale.forLanguageTag(normalized)
        val label = language.getDisplayLanguage(locale)
        return label.takeUnless { it.isBlank() || it.equals(normalized, true) }
            ?.replaceFirstChar { if (it.isLowerCase()) it.titlecase(locale) else it.toString() }
    }

    fun audioFormat(track: MediaTrack): String = listOfNotNull(
        when (track.codec?.lowercase(Locale.ROOT)) {
            "ac3" -> "Dolby Digital"
            "eac3" -> "Dolby Digital Plus"
            "truehd" -> "Dolby TrueHD"
            "dts" -> "DTS"
            "aac" -> "AAC"
            "flac" -> "FLAC"
            "mp3" -> "MP3"
            "opus" -> "Opus"
            else -> track.codec?.uppercase(Locale.ROOT)?.take(12)
        },
        when (track.channels) {
            1 -> "Mono"
            2 -> "Stereo"
            6 -> "5.1"
            8 -> "7.1"
            else -> null
        },
    ).joinToString(" · ")

    fun isSynchronized(track: MediaTrack): Boolean = Regex("(?i)\\b(synchronisiert|synchronized)\\b").containsMatchIn(track.displayTitle)
    fun isAiGenerated(track: MediaTrack): Boolean = Regex("(?i)\\b(ki|ai|grok|whisper)\\b").containsMatchIn(track.displayTitle)
    fun isCommentary(track: MediaTrack): Boolean = Regex("(?i)\\b(commentary|kommentar)\\b").containsMatchIn(track.displayTitle)
    fun isHearingImpaired(track: MediaTrack): Boolean = Regex("(?i)\\b(sdh|hearing impaired|hörgeschädigte)\\b").containsMatchIn(track.displayTitle)
}
