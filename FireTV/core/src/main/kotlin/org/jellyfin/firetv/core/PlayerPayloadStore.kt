package org.jellyfin.firetv.core

import java.util.UUID

/**
 * Hands the playback JSON from the WebView bridge to PlayerActivity without an
 * Intent extra. Live TV payloads carry the whole channel queue and can exceed
 * the Binder transaction limit (TransactionTooLargeException) when passed as
 * an Intent string.
 */
object PlayerPayloadStore {
    private val lock = Any()
    private var slot: Pair<String, String>? = null

    fun put(payload: String): String {
        val id = UUID.randomUUID().toString()
        synchronized(lock) {
            slot = id to payload
        }
        return id
    }

    fun take(id: String?): String? {
        if (id.isNullOrBlank()) {
            return null
        }
        return synchronized(lock) {
            if (slot?.first == id) slot?.second else null
        }
    }
}
