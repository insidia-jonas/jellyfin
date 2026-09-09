package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class PlayerCompatTest {
    @Test
    fun `queues the next and previous item from a play payload`() {
        val json = """
            {
              "ids":["a","b","c"],
              "items":[
                {"Id":"a","Name":"One","MediaType":"Video"},
                {"Id":"b","Name":"Two","MediaType":"Audio"},
                {"Id":"c","Name":"Three"}
              ],
              "serverAddress":"http://s:8096",
              "accessToken":"tok",
              "userId":"u"
            }
        """.trimIndent()
        assertEquals(listOf("a", "b", "c"), PlaybackPayload.itemIds(json))
        assertEquals("b", PlaybackPayload.nextItemId(json, "a"))
        assertEquals("a", PlaybackPayload.previousItemId(json, "b"))
        assertEquals(null, PlaybackPayload.nextItemId(json, "c"))
        val next = PlaybackPayload.retarget(json, "b")
        assertEquals("b", PlaybackPayload.itemId(next))
        assertEquals("Two", PlaybackPayload.itemName(next))
        assertEquals(listOf("a", "b", "c"), PlaybackPayload.itemIds(next))
        assertEquals("c", PlaybackPayload.nextItemId(next, "b"))
        assertTrue(PlaybackPayload.isAudio(next))
        assertEquals("http://s:8096", PlaybackPayload.serverAddress(next))
        assertEquals("tok", PlaybackPayload.accessToken(next))
    }

    @Test
    fun `player sync json is valid for the web plugin`() {
        val json = PlayerSyncState.json(
            event = "timeupdate",
            positionMs = 12_500,
            durationMs = 90_000,
            paused = false,
            volume = 80,
            itemId = "movie-1",
            isLive = false,
        )
        assertTrue(json.contains("\"event\":\"timeupdate\""))
        assertTrue(json.contains("\"positionMs\":12500"))
        assertTrue(json.contains("\"durationMs\":90000"))
        assertTrue(json.contains("\"paused\":false"))
        assertTrue(json.contains("\"volume\":80"))
        assertTrue(json.contains("\"itemId\":\"movie-1\""))
        assertFalse(json.contains("card:focus"))
    }

    @Test
    fun `native server list uses jellyfin-web findServers fields`() {
        val json = NativeServerList.toNativeShellJson(
            listOf(DiscoveredServer(address = "http://192.168.1.10:8096", id = "abc", name = "Home")),
        )
        assertTrue(json.contains("\"name\":\"Home\""))
        assertTrue(json.contains("\"id\":\"abc\""))
        assertTrue(json.contains("\"address\":\"http://192.168.1.10:8096\""))
        assertTrue(json.contains("\"Address\":\"http://192.168.1.10:8096\""))
    }
}
