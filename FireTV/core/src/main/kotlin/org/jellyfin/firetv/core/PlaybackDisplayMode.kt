package org.jellyfin.firetv.core

import kotlin.math.abs
import kotlin.math.round

data class PlaybackDisplayMode(val id: Int, val width: Int, val height: Int, val hz: Float) {
    companion object {
        /** Keep resolution; prefer an exact cadence at 50/60 Hz over low-refresh TV modes. */
        fun choose(fps: Float, current: PlaybackDisplayMode, modes: List<PlaybackDisplayMode>): PlaybackDisplayMode? {
            if (!fps.isFinite() || fps < 10 || fps > 120) return null
            fun matches(mode: PlaybackDisplayMode): Boolean {
                val multiple = round(mode.hz / fps)
                return multiple >= 1 && abs(mode.hz / fps - multiple) < 0.002f
            }
            if (matches(current) && current.hz >= 49) return current
            return modes.filter { it.width == current.width && it.height == current.height && matches(it) }
                .sortedWith(compareBy<PlaybackDisplayMode> { if (it.hz in 49f..61f) 0 else 1 }
                    .thenBy { abs(it.hz - 60f) }).firstOrNull()
        }
    }
}
