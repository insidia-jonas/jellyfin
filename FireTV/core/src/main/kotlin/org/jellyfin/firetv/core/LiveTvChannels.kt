package org.jellyfin.firetv.core

/**
 * Official Live TV channel list: `/LiveTv/Channels?addCurrentProgram=true`.
 * This is the contract the Fire TV overlay uses instead of movie-style IChannel cards.
 */
data class LiveTvChannel(
    val id: String,
    val name: String,
    val number: String? = null,
    val group: String? = null,
    val nowTitle: String? = null,
    val nowStart: String? = null,
    val nowEnd: String? = null,
    val nowOverview: String? = null,
    val imageTag: String? = null,
    val externalId: String? = null,
)

object LiveTvChannels {
    fun parse(body: String): List<LiveTvChannel> {
        val items = jsonArrayObjects(body, "Items").ifEmpty { jsonRootArrayObjects(body) }
        return items.mapNotNull { parseOne(it) }
    }

    fun parseOne(json: String): LiveTvChannel? {
        val id = jsonStringField(json, "Id") ?: jsonStringField(json, "id") ?: return null
        val name = jsonStringField(json, "Name") ?: jsonStringField(json, "name") ?: return null
        val program = jsonObjectField(json, "CurrentProgram")
        val tags = jsonStringArray(json, "Tags")
        return LiveTvChannel(
            id = id,
            name = name,
            number = jsonStringField(json, "Number")
                ?: jsonStringField(json, "ChannelNumber"),
            group = tags.firstOrNull()?.takeIf { it.isNotBlank() },
            nowTitle = program?.let { jsonStringField(it, "Name") },
            nowStart = program?.let { jsonStringField(it, "StartDate") },
            nowEnd = program?.let { jsonStringField(it, "EndDate") },
            nowOverview = program?.let { jsonStringField(it, "Overview") },
            imageTag = jsonObjectField(json, "ImageTags")?.let { jsonStringField(it, "Primary") },
            externalId = jsonStringField(json, "ExternalId"),
        )
    }

    fun clock(iso: String?): String? {
        if (iso.isNullOrBlank()) {
            return null
        }
        val time = iso.substringAfter('T', "").take(5)
        return time.takeIf { it.length == 5 && time[2] == ':' }
    }

    fun nowLine(channel: LiveTvChannel, german: Boolean = true): String? {
        val title = channel.nowTitle?.takeIf { it.isNotBlank() } ?: return null
        val start = clock(channel.nowStart)
        val end = clock(channel.nowEnd)
        val range = when {
            start != null && end != null -> " ($start–$end)"
            start != null -> " ($start)"
            else -> ""
        }
        val prefix = if (german) "Jetzt: " else "Now: "
        return prefix + title + range
    }
}
