package org.jellyfin.firetv.core

object SubtitleSync {
    data class Status(val message: String, val active: Boolean, val exists: Boolean, val state: String, val percent: Int)
    private fun request(current: ResolvedPlayback, route: String, ignoreSsl: Boolean, post: Boolean): JellyfinHttp.Response {
        val url = "${current.serverAddress.trimEnd('/')}/TreasureMaps/Subtitles/$route"
        return if (post) JellyfinHttp.post(url = url, body = "", accessToken = current.accessToken, ignoreSslErrors = ignoreSsl,
            deviceId = current.deviceId, deviceName = current.deviceName, appName = current.appName, appVersion = current.appVersion)
        else JellyfinHttp.get(url = url, accessToken = current.accessToken, ignoreSslErrors = ignoreSsl,
            deviceId = current.deviceId, deviceName = current.deviceName, appName = current.appName, appVersion = current.appVersion)
    }
    fun start(current: ResolvedPlayback, ignoreSsl: Boolean): String {
        val audio = current.selectedAudioIndex ?: current.audioTracks.firstOrNull { it.isDefault }?.index ?: current.audioTracks.firstOrNull()?.index
        require(audio != null && current.selectedSubtitleIndex != null) { "Bitte zuerst Ton und Untertitel wählen." }
        val response = request(current, "Sync?itemId=${current.itemId}&subtitleIndex=${current.selectedSubtitleIndex}&audioIndex=$audio", ignoreSsl, true)
        check(response.code == 202) { jsonStringField(response.body, "message") ?: "Tonspur-Abgleich derzeit nicht verfügbar (${response.code})." }
        return "Tonspur-Abgleich läuft auf dem Server. Du kannst den Film verlassen. Status unter Untertitelaufträge."
    }
    fun status(current: ResolvedPlayback, ignoreSsl: Boolean): String = readStatus(current, ignoreSsl).message

    fun readStatus(current: ResolvedPlayback, ignoreSsl: Boolean): Status {
        val response = request(current, "Jobs?itemId=${current.itemId}", ignoreSsl, false)
        check(response.code == 200) { "Untertitelaufträge derzeit nicht erreichbar." }
        val job = jsonArrayObjects(response.body, "jobs").firstOrNull { jsonStringField(it, "kind") == "sync" }
        return Status(job?.let { jsonStringField(it, "message") } ?: "Noch kein Tonspur-Abgleich für diesen Titel.",
            job?.let { jsonBooleanField(it, "active") } == true, job != null,
            job?.let { jsonStringField(it, "state") }.orEmpty(), job?.let { jsonLongField(it, "percent") }?.toInt()?.coerceIn(0, 100) ?: 0)
    }
}
