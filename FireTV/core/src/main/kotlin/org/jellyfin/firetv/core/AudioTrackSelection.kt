package org.jellyfin.firetv.core

data class PlayerAudioTrack(val language: String?, val channels: Int?)

object AudioTrackSelection {
    private fun language(value: String?): String? = when (val tag = value?.lowercase()?.substringBefore('-')) {
        "de", "deu", "ger" -> "de"
        "en", "eng" -> "en"
        "fr", "fra", "fre" -> "fr"
        "und", "", null -> null
        else -> tag
    }

    /** The container's audio order disambiguates same-language tracks and missing tags. */
    fun position(tracks: List<MediaTrack>, requested: Int, playerTracks: List<PlayerAudioTrack>): Int? {
        val source = tracks.sortedBy { it.index }
        val ordinal = source.indexOfFirst { it.index == requested }
        if (ordinal < 0) return null
        val wanted = source[ordinal]
        fun matches(actual: PlayerAudioTrack): Boolean =
            (language(wanted.language) == null || language(actual.language) == null || language(wanted.language) == language(actual.language)) &&
                (wanted.channels == null || actual.channels == null || wanted.channels == actual.channels)
        if (source.size == playerTracks.size && matches(playerTracks[ordinal])) return ordinal
        return playerTracks.indices.filter { matches(playerTracks[it]) }.singleOrNull()
    }
}
