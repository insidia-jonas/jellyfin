package org.jellyfin.firetv.player

/**
 * Forwards transport commands from the web plugin to the running [PlayerActivity].
 */
object PlayerCommands {
    @Volatile
    var listener: Listener? = null

    interface Listener {
        fun pause()
        fun resume()
        fun stop()
        fun seekMs(positionMs: Long)
        fun setVolume(percent: Int)
        fun destroy()
    }

    fun pause() = listener?.pause()
    fun resume() = listener?.resume()
    fun stop() = listener?.stop()
    fun seekMs(positionMs: Long) = listener?.seekMs(positionMs)
    fun setVolume(percent: Int) = listener?.setVolume(percent)
    fun destroy() = listener?.destroy()
}
