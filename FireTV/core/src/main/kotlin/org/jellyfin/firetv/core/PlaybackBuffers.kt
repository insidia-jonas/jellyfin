package org.jellyfin.firetv.core

/** Values passed to Media3; both start thresholds must fit in the minimum buffer. */
data class PlaybackBuffers(
    val minMs: Int,
    val maxMs: Int,
    val startMs: Int,
    val rebufferMs: Int,
    val targetBytes: Int = 64 * 1024 * 1024,
) {
    init {
        require(startMs >= 0 && rebufferMs >= 0)
        require(minMs >= startMs && minMs >= rebufferMs && maxMs >= minMs)
    }

    companion object {
        fun forPlayback(live: Boolean): PlaybackBuffers =
            // Start promptly, but refill files well before they run dry. The byte cap
            // bounds memory for high-bitrate video on older Fire TV devices.
            if (live) PlaybackBuffers(2_500, 15_000, 1_000, 2_500)
            else PlaybackBuffers(30_000, 60_000, 1_000, 5_000)
    }
}
