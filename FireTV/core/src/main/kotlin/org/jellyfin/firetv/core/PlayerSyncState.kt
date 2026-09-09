package org.jellyfin.firetv.core

/**
 * JSON the native ExoPlayer posts into jellyfin-web so the hosted player
 * plugin can emit the same events htmlVideoPlayer would.
 */
object PlayerSyncState {
    fun json(
        event: String,
        positionMs: Long,
        durationMs: Long,
        paused: Boolean,
        volume: Int = 100,
        itemId: String? = null,
        isLive: Boolean = false,
    ): String = buildString {
        append('{')
        append("\"event\":").append(jsonEscape(event)).append(',')
        append("\"positionMs\":").append(positionMs.coerceAtLeast(0)).append(',')
        append("\"durationMs\":").append(durationMs.coerceAtLeast(0)).append(',')
        append("\"paused\":").append(paused).append(',')
        append("\"volume\":").append(volume.coerceIn(0, 100)).append(',')
        append("\"isLive\":").append(isLive)
        if (!itemId.isNullOrBlank()) {
            append(",\"itemId\":").append(jsonEscape(itemId))
        }
        append('}')
    }
}
