package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class LiveTvNowNextTextTest {
    @Test
    fun `parses the server now next tile contract`() {
        val guide = LiveTvNowNextText.parse(
            name = "Das Erste  ·  Tagesschau",
            originalTitle = "Das Erste",
            overview = """
                Jetzt: Tagesschau (20:00–20:15)
                Danach: Tagesthemen (20:15–21:00)

                Nachrichten aus Deutschland.
            """.trimIndent(),
        )
        assertEquals("Das Erste", guide.channelName)
        assertEquals("Tagesschau", guide.nowTitle)
        assertEquals("20:00–20:15", guide.nowRange)
        assertEquals("Tagesthemen", guide.nextTitle)
        assertEquals("20:15–21:00", guide.nextRange)
        assertEquals("Nachrichten aus Deutschland.", guide.plot)
        assertEquals("Jetzt: Tagesschau (20:00–20:15)", guide.nowLine())
        assertEquals("Danach: Tagesthemen (20:15–21:00)", guide.nextLine())
        assertFalse(guide.isGroupFolder)
        assertTrue(
            LiveTvNowNextText.looksLikeLibraryTile(
                "Das Erste  ·  Tagesschau",
                "Das Erste",
                "Jetzt: Tagesschau (20:00–20:15)",
            ),
        )
    }

    @Test
    fun `parses group folders as sender counts`() {
        val guide = LiveTvNowNextText.parse(
            name = "Sport  ·  12",
            originalTitle = null,
            overview = "12 Sender",
        )
        assertEquals("Sport", guide.channelName)
        assertTrue(guide.isGroupFolder)
        assertEquals(12, guide.groupCount)
        assertEquals("12 Sender", guide.folderLine())
        assertNull(guide.nowTitle)
    }

    @Test
    fun `falls back to the card name when the guide is empty`() {
        val guide = LiveTvNowNextText.parse("ZDF", null, "")
        assertEquals("ZDF", guide.channelName)
        assertNull(guide.nowTitle)
        assertEquals("ZDF", LiveTvNowNextText.channelTitle("ZDF", null, null))
    }
}
