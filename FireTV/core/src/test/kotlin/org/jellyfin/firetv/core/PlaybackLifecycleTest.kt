package org.jellyfin.firetv.core

import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class PlaybackLifecycleTest {
    @Test
    fun `live and movie buffer thresholds satisfy Media3 constraints`() {
        listOf(true, false).forEach { live ->
            val policy = PlaybackBuffers.forPlayback(live)
            assertTrue(policy.minMs >= policy.startMs)
            assertTrue(policy.minMs >= policy.rebufferMs)
            assertTrue(policy.maxMs >= policy.minMs)
        }
        // Values from the crash on the Cube must never reach DefaultLoadControl.
        assertThrows(IllegalArgumentException::class.java) { PlaybackBuffers(2500, 15000, 1000, 3000) }
    }

    @Test
    fun `file playback keeps a network outage reserve without waiting for it at startup`() {
        val file = PlaybackBuffers.forPlayback(false)
        // A stalled read can take 12 seconds before Media3 resumes its HTTP range.
        assertTrue(file.minMs >= 2 * 12_000)
        assertTrue(file.startMs <= 1_500)
        assertTrue(file.rebufferMs > file.startMs)
        assertTrue(file.targetBytes <= 64 * 1024 * 1024)
        assertTrue(PlaybackBuffers.forPlayback(true).minMs < file.minMs)
    }

    @Test
    fun `cancellation disconnects off the caller thread and rejects future work`() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val caller = Thread.currentThread()
        var disconnectThread: Thread? = null
        val connection = object : HttpURLConnection(URL("http://localhost/")) {
            override fun connect() = Unit
            override fun usingProxy() = false
            override fun disconnect() {
                disconnectThread = Thread.currentThread()
                entered.countDown()
                release.await(3, TimeUnit.SECONDS)
            }
        }
        val cancellation = HttpCancellation()
        cancellation.attach(connection)
        try {
            cancellation.cancel()
            assertTrue(cancellation.isCancelled)
            assertTrue(entered.await(2, TimeUnit.SECONDS))
            assertNotSame(caller, disconnectThread)
            assertThrows(CancellationException::class.java) { cancellation.attach(connection) }
            assertThrows(CancellationException::class.java) { cancellation.checkActive() }
        } finally { release.countDown() }
    }

    @Test
    fun `zapping from the middle preserves the original channel order`() {
        val payload = """{"ids":["one","two","three"],"items":[{"Id":"two","Name":"Two","Type":"TvChannel"},{"Id":"three"},{"Id":"one"}]}"""
        assertEquals("two", PlaybackPayload.itemId(payload))
        assertEquals("one", PlaybackPayload.previousItemId(payload, "two"))
        assertEquals("three", PlaybackPayload.nextItemId(payload, "two"))
        val retargeted = PlaybackPayload.retarget(payload, "three")
        assertEquals("three", PlaybackPayload.itemId(retargeted))
        assertEquals(listOf("one", "two", "three"), PlaybackPayload.itemIds(retargeted))
    }
}
