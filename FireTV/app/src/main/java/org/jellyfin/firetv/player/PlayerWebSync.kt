package org.jellyfin.firetv.player

/**
 * Pushes ExoPlayer clock/state into the hosted jellyfin-web plugin.
 */
object PlayerWebSync {
    @Volatile
    var sink: ((String) -> Unit)? = null

    fun emit(json: String) {
        sink?.invoke(json)
    }
}
