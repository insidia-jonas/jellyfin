package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.util.concurrent.Executor

class PlaybackReportQueueTest {
    @Test
    fun `delayed progress is coalesced and cannot overwrite final position after stopping`() {
        val tasks = ArrayDeque<Runnable>()
        val reports = mutableListOf<Pair<String, Long>>()
        val queue = PlaybackReportQueue({ path, pos, _, _ -> reports.add(path to pos) }, Executor { tasks.add(it) })
        queue.progress(0, true) // Player prepares before session starts.
        queue.playing(123_000)
        queue.progress(124_000, false)
        queue.progress(140_000, true)
        queue.stopped(145_000, false)
        queue.progress(0, true) // Late player callback after release.
        queue.stopped(0, false)
        while (tasks.isNotEmpty()) tasks.removeFirst().run()
        assertEquals(listOf("" to 123_000L, "/Progress" to 140_000L, "/Stopped" to 145_000L), reports)
    }

    @Test
    fun `replacement tune waits until the server has processed the final stop`() {
        val tasks = ArrayDeque<Runnable>()
        val events = mutableListOf<String>()
        val queue = PlaybackReportQueue({ path, _, _, _ -> events.add(path); true }, Executor { tasks.add(it) })
        queue.playing(0)
        queue.progress(1000, false)
        val closed = queue.stopped(2000, true)
        closed.thenRun { events.add("open replacement") }
        assertSame(closed, queue.stopped(3000, false))
        tasks.removeFirst().run()
        tasks.removeFirst().run()
        assertFalse(closed.isDone)
        tasks.removeFirst().run()
        assertTrue(closed.get())
        assertEquals(listOf("", "/Progress", "/Stopped", "open replacement"), events)
        assertTrue(tasks.isEmpty())
    }

    @Test
    fun `failed stop delivery completes without a duplicate close request or hung waiter`() {
        for (throws in listOf(false, true)) {
            val queue = PlaybackReportQueue({ path, _, _, _ ->
                if (path == "/Stopped" && throws) error("connection lost")
                path != "/Stopped"
            }, Executor { it.run() })
            queue.playing(0)
            val closed = queue.stopped(1000, true)
            assertTrue(closed.isDone)
            assertFalse(closed.get())
            assertSame(closed, queue.stopped(1000, true))
        }
    }
}
