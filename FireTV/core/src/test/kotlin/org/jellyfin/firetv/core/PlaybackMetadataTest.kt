package org.jellyfin.firetv.core

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import java.util.concurrent.CancellationException
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class PlaybackMetadataTest {
    @Test
    fun `autoplay preserves series identity episode number and German plot`() {
        val payload = """{"ids":["five","six"],"items":[
            {"Id":"five","Name":"Folge 5","Type":"Episode","SeriesName":"All Her Fault","ParentIndexNumber":1,"IndexNumber":5},
            {"Id":"six","Name":"Folge 6","Type":"Episode","SeriesName":"All Her Fault","ParentIndexNumber":1,"IndexNumber":6,"Overview":"Die Familie sucht Antworten."}
        ]}"""
        val next = PlaybackPayload.retarget(payload, "six")
        val metadata = PlaybackMetadata.fromPayload(next)
        assertEquals("All Her Fault", metadata.title)
        assertEquals("S01E06", metadata.episodeLine)
        assertEquals("Die Familie sucht Antworten.", metadata.overview)
        assertFalse(metadata.needsLookup)
        assertEquals(listOf("five", "six"), PlaybackPayload.itemIds(next))
    }

    @Test
    fun `specials double episodes and real episode names remain distinguishable`() {
        val metadata = PlaybackMetadata.fromItem("""{"Name":"Ein neuer Anfang","Type":"Episode","SeriesName":"Serie","ParentIndexNumber":0,"IndexNumber":1,"IndexNumberEnd":2}""")
        assertEquals("Serie", metadata.title)
        assertEquals("S00E01–02 · Ein neuer Anfang", metadata.episodeLine)
        val withoutSeries = PlaybackMetadata.fromItem("""{"Name":"Ein neuer Anfang","Type":"Episode","IndexNumber":3}""")
        assertEquals("Ein neuer Anfang", withoutSeries.title)
        assertEquals("E03", withoutSeries.episodeLine)
        assertTrue(withoutSeries.needsLookup)
    }

    @Test
    fun `ID only queues request metadata without presenting the GUID as a title`() {
        val payload = """{"ids":["five","six"],"items":[{"Id":"five","Name":"Folge 5","Type":"Episode"}]}"""
        val next = PlaybackMetadata.fromPayload(PlaybackPayload.retarget(payload, "six"))
        assertEquals("Jellyfin", next.title)
        assertTrue(next.needsLookup)
        val movie = PlaybackMetadata.fromItem("""{"Name":"Ankunft","Type":"Movie","OriginalTitle":"Arrival"}""")
        assertEquals("Ankunft", movie.title)
        assertNull(movie.episodeLine)
    }

    @Test
    fun `metadata lookup is authenticated item scoped and does not open playback`() {
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        var calls = 0
        var token = ""
        var body = """{"Id":"six","Name":"Folge 6","Type":"Episode","SeriesName":"All Her Fault","ParentIndexNumber":1,"IndexNumber":6,"Overview":"Antworten."}"""
        server.createContext("/Users/u/Items/six") { exchange ->
            calls++
            token = exchange.requestHeaders.getFirst("X-Emby-Token")
            val bytes = body.toByteArray()
            exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try {
            val current = ResolvedPlayback("", "Jellyfin", "six", null, null, 0, "DirectPlay",
                "http://127.0.0.1:${server.address.port}", "private", "u", "device", "TV", "client", "version")
            val metadata = PlaybackMetadata.fetch(current, false, HttpCancellation())!!
            assertEquals("private", token)
            assertEquals("All Her Fault", metadata.title)
            assertEquals(1, calls)
            body = """{"Id":"another-episode","Name":"Wrong"}"""
            assertNull(PlaybackMetadata.fetch(current, false, HttpCancellation()))
            val cancelled = HttpCancellation().apply { cancel() }
            assertThrows(CancellationException::class.java) { PlaybackMetadata.fetch(current, false, cancelled) }
            assertEquals(2, calls)
            assertNull(PlaybackMetadata.fetch(current.copy(isLive = true), false, HttpCancellation()))
            assertEquals(2, calls)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun `partial enrichment retains known episode identity`() {
        val known = PlaybackMetadata(name = "Folge 6", type = "Episode", seriesName = "All Her Fault", season = 1, episode = 6)
        val updated = known.enrichedBy(PlaybackMetadata(overview = "Eine Beschreibung."))
        assertEquals("All Her Fault", updated.title)
        assertEquals("S01E06", updated.episodeLine)
        assertEquals("Eine Beschreibung.", updated.overview)
    }
}
