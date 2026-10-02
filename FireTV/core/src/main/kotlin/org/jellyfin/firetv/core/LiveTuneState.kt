package org.jellyfin.firetv.core

/** Main-thread state: navigation follows the pending selection, not the old picture. */
class LiveTuneState(private val clockMs: () -> Long = { System.nanoTime() / 1_000_000 }) {
    private var recoveryStartedAt: Long? = null
    var generation: Long = 0
        private set
    var selectedId: String? = null
        private set
    var retries: Int = 0
        private set

    fun select(id: String?, resetRetries: Boolean = true): Long {
        selectedId = id
        if (resetRetries) {
            retries = 0
            recoveryStartedAt = clockMs()
        }
        return ++generation
    }

    fun accepts(request: Long): Boolean = generation == request

    fun invalidate() { generation++ }

    fun retryDelayMs(): Long? {
        if (recoveryStartedAt == null) recoveryStartedAt = clockMs()
        if (retries >= 3 || remainingMs() <= 1_000) return null
        val delay = (1_000L shl retries).coerceAtMost(4_000)
        if (remainingMs() <= delay) return null
        retries++
        return delay
    }

    fun attemptTimeoutMs(maximum: Long): Long {
        if (recoveryStartedAt == null) recoveryStartedAt = clockMs()
        return remainingMs().coerceIn(1, maximum)
    }

    private fun remainingMs(): Long = recoveryStartedAt?.let { 45_000 - (clockMs() - it) } ?: 45_000

    fun stablePlayback() { retries = 0; recoveryStartedAt = null }
}
