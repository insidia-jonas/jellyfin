package org.jellyfin.firetv.core

/** Scrub locally and commit one seek on OK; Back leaves playback untouched. */
class PlaybackSeek {
    var previewMs: Long? = null
        private set

    fun move(positionMs: Long, durationMs: Long, forward: Boolean, repeats: Int): Long {
        val step = when { repeats >= 18 -> 60_000L; repeats >= 6 -> 30_000L; else -> 10_000L }
        return preview(positionMs, durationMs, (previewMs ?: positionMs) + if (forward) step else -step)
    }

    fun preview(positionMs: Long, durationMs: Long, targetMs: Long): Long {
        val end = if (durationMs > 0) (durationMs - 1000).coerceAtLeast(0) else positionMs.coerceAtLeast(0)
        return targetMs.coerceIn(0, end).also { previewMs = it }
    }

    fun take(): Long? = previewMs.also { previewMs = null }
    fun cancel() { previewMs = null }
}
