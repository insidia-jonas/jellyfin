package org.jellyfin.firetv.core

import java.net.URLEncoder

/**
 * Jellyfin remote-subtitle API (Open Subtitles plugin and similar providers).
 * Search and download go through the server — never directly to a provider.
 */
data class RemoteSubtitle(
    val id: String,
    val name: String?,
    val language: String?,
    val format: String?,
    val providerName: String?,
    val communityRating: Double?,
    val downloadCount: Int?,
    val isHashMatch: Boolean,
    val hearingImpaired: Boolean,
) {
    fun label(): String {
        val bits = mutableListOf<String>()
        name?.takeIf { it.isNotBlank() }?.let { bits += it }
        format?.takeIf { it.isNotBlank() }?.let { bits += it.uppercase() }
        if (isHashMatch) {
            bits += "match"
        }
        if (hearingImpaired) {
            bits += "CC"
        }
        providerName?.takeIf { it.isNotBlank() }?.let { bits += it }
        downloadCount?.takeIf { it > 0 }?.let { bits += "${it}×" }
        communityRating?.takeIf { it > 0 }?.let { bits += String.format("%.1f", it) }
        return bits.joinToString(" · ").ifBlank { id }
    }
}

sealed class SubtitleSearchOutcome {
    data class Success(val items: List<RemoteSubtitle>) : SubtitleSearchOutcome()
    data class Failed(val httpCode: Int, val message: String) : SubtitleSearchOutcome()
}

object RemoteSubtitles {
    val SEARCH_LANGUAGES: List<Pair<String, String>> = listOf(
        "ger" to "Deutsch",
        "eng" to "English",
        "fra" to "Français",
        "spa" to "Español",
        "ita" to "Italiano",
        "tur" to "Türkçe",
        "pol" to "Polski",
        "por" to "Português",
        "rus" to "Русский",
        "jpn" to "日本語",
        "dut" to "Nederlands",
        "chi" to "中文",
    )

    fun preferredLanguage(languageTag: String?): String {
        val tag = languageTag.orEmpty().lowercase()
        val two = tag.take(2)
        return when (two) {
            "de" -> "ger"
            "en" -> "eng"
            "fr" -> "fra"
            "es" -> "spa"
            "it" -> "ita"
            "tr" -> "tur"
            "pl" -> "pol"
            "pt" -> "por"
            "ru" -> "rus"
            "ja" -> "jpn"
            "nl" -> "dut"
            "zh" -> "chi"
            else -> SEARCH_LANGUAGES.firstOrNull { it.first == tag.take(3) }?.first ?: "eng"
        }
    }

    fun parseSearchBody(body: String): List<RemoteSubtitle> {
        return jsonRootArrayObjects(body).mapNotNull { parseOne(it) }
    }

    fun search(
        serverAddress: String,
        accessToken: String,
        itemId: String,
        language: String,
        ignoreSslErrors: Boolean,
        deviceId: String = "",
        deviceName: String = "Fire TV",
        appName: String = FireTvClient.APP_NAME,
        appVersion: String = FireTvClient.APP_VERSION,
    ): SubtitleSearchOutcome {
        val lang = language.ifBlank { "eng" }
        val url = "${serverAddress.trimEnd('/')}/Items/$itemId/RemoteSearch/Subtitles/$lang"
        val response = JellyfinHttp.get(
            url = url,
            accessToken = accessToken,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            readTimeoutMs = 25_000,
        )
        if (response.code !in 200..299) {
            return SubtitleSearchOutcome.Failed(response.code, response.body.take(240))
        }
        return SubtitleSearchOutcome.Success(parseSearchBody(response.body))
    }

    fun download(
        serverAddress: String,
        accessToken: String,
        itemId: String,
        subtitleId: String,
        ignoreSslErrors: Boolean,
        deviceId: String = "",
        deviceName: String = "Fire TV",
        appName: String = FireTvClient.APP_NAME,
        appVersion: String = FireTvClient.APP_VERSION,
    ): Boolean {
        val encoded = URLEncoder.encode(subtitleId, Charsets.UTF_8.name()).replace("+", "%20")
        val url = "${serverAddress.trimEnd('/')}/Items/$itemId/RemoteSearch/Subtitles/$encoded"
        val response = JellyfinHttp.post(
            url = url,
            body = "",
            accessToken = accessToken,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            readTimeoutMs = 45_000,
        )
        return response.code in 200..299
    }

    private fun parseOne(json: String): RemoteSubtitle? {
        val id = jsonStringField(json, "Id") ?: jsonStringField(json, "id") ?: return null
        if (id.isBlank()) {
            return null
        }
        return RemoteSubtitle(
            id = id,
            name = jsonStringField(json, "Name"),
            language = jsonStringField(json, "ThreeLetterISOLanguageName")
                ?: jsonStringField(json, "Language"),
            format = jsonStringField(json, "Format"),
            providerName = jsonStringField(json, "ProviderName"),
            communityRating = jsonDoubleField(json, "CommunityRating"),
            downloadCount = jsonLongField(json, "DownloadCount")?.toInt(),
            isHashMatch = jsonBooleanField(json, "IsHashMatch") == true,
            hearingImpaired = jsonBooleanField(json, "HearingImpaired") == true,
        )
    }
}
