package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class LiveTvChannelsTest {
    @Test
    fun `parses official channels with current program`() {
        val body = """
            {
              "Items": [
                {
                  "Id": "ch-1",
                  "Name": "Das Erste",
                  "Number": "1",
                  "Type": "TvChannel",
                  "ExternalId": "m3u_ard",
                  "ImageTags": { "Primary": "tag-1" },
                  "CompletionPercentage": 40.0,
                  "CurrentProgram": {
                    "Name": "Tagesschau",
                    "StartDate": "2026-09-09T20:00:00.0000000Z",
                    "EndDate": "2026-09-09T20:15:00.0000000Z",
                    "Overview": "Nachrichten",
                    "CompletionPercentage": 40.0
                  },
                  "NextProgram": {
                    "Name": "Wetter",
                    "StartDate": "2026-09-09T20:15:00.0000000Z",
                    "EndDate": "2026-09-09T20:20:00.0000000Z"
                  }
                }
              ]
            }
        """.trimIndent()
        val channels = LiveTvChannels.parse(body)
        assertEquals(1, channels.size)
        assertEquals("ch-1", channels[0].id)
        assertEquals("Das Erste", channels[0].name)
        assertEquals("1", channels[0].number)
        assertEquals("Tagesschau", channels[0].nowTitle)
        assertEquals("20:00", LiveTvChannels.clock(channels[0].nowStart))
        assertTrue(LiveTvChannels.nowLine(channels[0])!!.contains("Tagesschau"))
        assertEquals("Wetter", channels[0].nextTitle)
        assertTrue(LiveTvChannels.nextLine(channels[0])!!.contains("Wetter"))
        assertEquals(40.0, channels[0].progressPercent)
        assertEquals(
            40.0,
            LiveTvChannels.progressPercent(
                channels[0],
                java.time.Instant.parse("2026-09-09T20:06:00Z").toEpochMilli(),
            ),
        )
        assertEquals("m3u_ard", channels[0].externalId)
    }
}
