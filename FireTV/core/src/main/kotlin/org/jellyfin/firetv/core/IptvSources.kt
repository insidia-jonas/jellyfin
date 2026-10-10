package org.jellyfin.firetv.core

data class IptvSource(
    val id: String, val mediaSourceId: String, val name: String, val status: String,
    val channelSpecific: Boolean, val checkedAt: String?, val startMs: Long?, val isDefault: Boolean,
)

data class IptvSourceSnapshot(val automaticId: String, val state: String, val accountReason: String?, val sources: List<IptvSource>, val playingId: String? = null)

/** A menu read only fetches cached observations from Jellyfin, never from the IPTV provider. */
object IptvSources {
    fun load(payload: String, ignoreSsl: Boolean, cancellation: HttpCancellation, liveStreamId: String? = null): IptvSourceSnapshot {
        val server = PlaybackPayload.serverAddress(payload) ?: error("Missing server")
        val item = PlaybackPayload.itemId(payload) ?: error("Missing channel")
        val response = JellyfinHttp.get(
            "$server/LiveTv/Channels/${java.net.URLEncoder.encode(item, "UTF-8")}/Sources" +
                (liveStreamId?.let { "?liveStreamId=" + java.net.URLEncoder.encode(it, "UTF-8") } ?: ""),
            accessToken = PlaybackPayload.accessToken(payload), ignoreSslErrors = ignoreSsl,
            deviceId = jsonStringField(payload, "deviceId").orEmpty(),
            connectTimeoutMs = 4_000, readTimeoutMs = 7_000, cancellation = cancellation,
        )
        require(response.code in 200..299) { "Server selection HTTP ${response.code}" }
        return parse(response.body)
    }

    fun parse(body: String): IptvSourceSnapshot = IptvSourceSnapshot(
        jsonStringField(body, "AutomaticMediaSourceId").orEmpty(),
        jsonStringField(body, "State").orEmpty(), jsonStringField(body, "AccountReason"),
        jsonArrayObjects(body, "Sources").take(16).mapNotNull { row ->
            val id = jsonStringField(row, "MediaSourceId")?.takeIf { it.isNotBlank() } ?: return@mapNotNull null
            IptvSource(jsonStringField(row, "Id").orEmpty(), id,
                jsonStringField(row, "Name").orEmpty(), jsonStringField(row, "Status") ?: "Unknown",
                jsonBooleanField(row, "ChannelSpecific") == true, jsonStringField(row, "LastCheckedUtc"),
                jsonLongField(row, "MedianStartMilliseconds"), jsonBooleanField(row, "IsDefault") == true)
        },
        jsonStringField(body, "PlayingSourceId"),
    )
}
