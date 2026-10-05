package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
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
}
