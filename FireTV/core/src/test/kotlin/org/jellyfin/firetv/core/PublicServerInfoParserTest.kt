package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class PublicServerInfoParserTest {
    @Test
    fun `parses system info public payload`() {
        val json = """
            {"LocalAddress":"http://192.168.1.20:8096","ServerName":"Media","Version":"10.11.0",
             "ProductName":"Jellyfin Server","Id":"server-id","StartupWizardCompleted":true}
        """.trimIndent()
        val info = PublicServerInfoParser.parse(json)!!
        assertEquals("Media", info.serverName)
        assertEquals("10.11.0", info.version)
        assertEquals("server-id", info.id)
        assertTrue(info.isJellyfin)
    }

    @Test
    fun `rejects unrelated html or empty bodies`() {
        assertEquals(null, PublicServerInfoParser.parse("<html>not jellyfin</html>"))
        assertEquals(null, PublicServerInfoParser.parse(""))
        assertFalse(PublicServerInfo(null, null, null, "Plex").isJellyfin)
    }
}
