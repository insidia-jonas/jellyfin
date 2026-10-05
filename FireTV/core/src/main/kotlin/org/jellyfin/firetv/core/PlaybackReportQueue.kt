package org.jellyfin.firetv.core

import java.util.concurrent.Executor
import java.util.concurrent.Executors

/** Ordered, activity-independent delivery. A late progress call cannot revive a stopped session. */
class PlaybackReportQueue(
    private val deliver: (String, Long, Boolean, Boolean) -> Unit,
    private val executor: Executor = sharedExecutor,
) {
    private var started = false
    private var closed = false
    private var progressPending = false
    private var latestPosition = 0L
    private var latestPaused = false

    @Synchronized
    fun playing(positionMs: Long) {
        if (started || closed) return
        started = true
        executor.execute { deliver("", positionMs.coerceAtLeast(0), false, false) }
    }

    @Synchronized
    fun progress(positionMs: Long, paused: Boolean) {
        if (!started || closed) return
        latestPosition = positionMs.coerceAtLeast(0)
        latestPaused = paused
        if (progressPending) return
        progressPending = true
        executor.execute {
            val snapshot = synchronized(this) {
                progressPending = false
                latestPosition to latestPaused
            }
            deliver("/Progress", snapshot.first, snapshot.second, false)
        }
    }

    @Synchronized
    fun stopped(positionMs: Long, failed: Boolean) {
        if (!started || closed) return
        closed = true
        executor.execute { deliver("/Stopped", positionMs.coerceAtLeast(0), true, failed) }
    }

    companion object {
        private val sharedExecutor = Executors.newSingleThreadExecutor { task ->
            Thread(task, "jellyfin-playback-reports").apply { isDaemon = true }
        }
    }
}
