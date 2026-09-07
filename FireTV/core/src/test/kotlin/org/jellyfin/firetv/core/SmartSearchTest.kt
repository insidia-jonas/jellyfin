package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class SmartSearchTest {
    @Test
    fun `ranks an exact movie title above a weak token match`() {
        val ranked = SmartSearch.rank(
            listOf(
                SmartSearch.Candidate("2", "Treasure Planet", "Folder"),
                SmartSearch.Candidate("1", "Arrival", "Movie", year = 2016, communityRating = 8.0),
            ),
            "Arrival 2016",
        )
        assertEquals("1", ranked.first().id)
    }

    @Test
    fun `boosts items from the open library such as Treasure Maps`() {
        val ranked = SmartSearch.rank(
            listOf(
                SmartSearch.Candidate("global", "Dune", "Movie", parentId = "other"),
                SmartSearch.Candidate("local", "Dune", "Movie", parentId = "treasure-maps"),
            ),
            "Dune",
            preferredParentId = "treasure-maps",
        )
        assertEquals("local", ranked.first().id)
    }

    @Test
    fun `normalizes german articles and umlauts`() {
        val parsed = SmartSearch.parseQuery("Die unendliche Geschichte 1984")
        assertEquals(1984, parsed.year)
        assertTrue(parsed.normalized.contains("unendliche"))
        assertFalse(parsed.normalized.startsWith("die"))
        assertTrue(SmartSearch.variants("Für Elise").any { it.contains("fuer") || it.contains("für") })
    }

    @Test
    fun `detects failed home categories`() {
        assertTrue(SmartSearch.looksFailed("Failed to retrieve sections"))
        assertTrue(SmartSearch.looksFailed("Request failed"))
        assertTrue(SmartSearch.looksFailed("Kategorie fehlgeschlagen"))
        assertFalse(SmartSearch.looksFailed("Continue Watching"))
    }
}
