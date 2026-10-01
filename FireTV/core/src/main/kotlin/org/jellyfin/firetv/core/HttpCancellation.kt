package org.jellyfin.firetv.core

import java.net.HttpURLConnection
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors

/** Cancels blocking HTTP work without blocking the Android main thread. */
class HttpCancellation {
    @Volatile
    var isCancelled: Boolean = false
        private set
    private val connections = mutableSetOf<HttpURLConnection>()

    @Synchronized
    fun attach(connection: HttpURLConnection) {
        checkActive()
        connections.add(connection)
    }

    @Synchronized
    fun detach(connection: HttpURLConnection) {
        connections.remove(connection)
    }

    fun checkActive() {
        if (isCancelled) throw CancellationException("Playback request superseded")
    }

    fun cancel() {
        val pending = synchronized(this) {
            isCancelled = true
            connections.toList().also { connections.clear() }
        }
        pending.forEach { connection -> disconnects.execute { runCatching { connection.disconnect() } } }
    }

    companion object {
        private val disconnects = Executors.newCachedThreadPool { task ->
            Thread(task, "firetv-http-cancel").apply { isDaemon = true }
        }
    }
}
