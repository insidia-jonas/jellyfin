package org.jellyfin.firetv.core

/** Values passed to Media3; both start thresholds must fit in the minimum buffer. */
data class PlaybackBuffers(
    val minMs: Int,
    val maxMs: Int,
    val startMs: Int,
    val rebufferMs: Int,
) {
    init {
        require(startMs >= 0 && rebufferMs >= 0)
        require(minMs >= startMs && minMs >= rebufferMs && maxMs >= minMs)
    }

    companion object {
        fun forPlayback(live: Boolean): PlaybackBuffers =
            PlaybackBuffers(if (live) 2_500 else 3_000, if (live) 15_000 else 20_000, 1_000, 2_500)
    }
}
