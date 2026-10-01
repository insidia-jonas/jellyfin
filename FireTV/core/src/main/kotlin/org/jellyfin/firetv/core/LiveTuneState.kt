package org.jellyfin.firetv.core

/** Main-thread state: navigation follows the pending selection, not the old picture. */
class LiveTuneState {
    var generation: Long = 0
        private set
    var selectedId: String? = null
        private set
    var retries: Int = 0
        private set

    fun select(id: String?, resetRetries: Boolean = true): Long {
        selectedId = id
        if (resetRetries) retries = 0
        return ++generation
    }

    fun accepts(request: Long): Boolean = generation == request

    fun invalidate() { generation++ }

    fun retryDelayMs(): Long? {
        if (retries >= 3) return null
        return (1_000L shl retries++).coerceAtMost(4_000)
    }

    fun stablePlayback() { retries = 0 }
}
