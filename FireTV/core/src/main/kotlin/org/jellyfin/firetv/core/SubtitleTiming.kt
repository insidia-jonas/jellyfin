package org.jellyfin.firetv.core

import java.security.MessageDigest

/** Positive milliseconds display subtitles later. Persist separately for every account/source/track. */
object SubtitleTiming {
    const val LIMIT_MS = 120_000L
    fun clamp(value: Long) = value.coerceIn(-LIMIT_MS, LIMIT_MS)
    fun key(server: String, user: String, item: String, source: String?, track: Int, trackIdentity: String? = null): String {
        val identity = listOf(server.trimEnd('/'), user, item, source.orEmpty(), trackIdentity ?: track.toString()).joinToString("\n")
        return MessageDigest.getInstance("SHA-256").digest(identity.toByteArray()).joinToString("") { "%02x".format(it) }
    }
}
