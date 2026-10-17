package org.jellyfin.firetv.core

/** One audio conversion attempt per title; network/video errors keep their own handling. */
class AudioRecovery {
    private var itemId: String? = null
    var enabled: Boolean = false
        private set

    fun select(id: String?, newPlayback: Boolean) {
        if (newPlayback || id != itemId) enabled = false
        itemId = id
    }

    fun requireCompatibleAudio() { enabled = true }

    fun recover(errorCode: Int, audioRenderer: Boolean, live: Boolean): Boolean {
        val audioFailure = errorCode in 5001..5004 || (audioRenderer && errorCode in 4001..4005)
        if (live || enabled || !audioFailure) return false
        enabled = true
        return true
    }
}
