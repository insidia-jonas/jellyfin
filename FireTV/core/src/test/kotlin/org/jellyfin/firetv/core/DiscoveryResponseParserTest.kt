package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class DiscoveryResponseParserTest {
    @Test
    fun `parses pascal case discovery json`() {
        val json = """{"Address":"http://10.0.0.5:8096","Id":"abc-123","Name":"Living Room"}"""
        val server = DiscoveryResponseParser.parse(json)!!
        assertEquals("http://10.0.0.5:8096", server.address)
        assertEquals("abc-123", server.id)
        assertEquals("Living Room", server.name)
    }

    @Test
    fun `parses camel case discovery json`() {
        val json = """{"address":"http://10.0.0.8:8096","id":"id-1","name":"NAS"}"""
        val server = DiscoveryResponseParser.parse(json)!!
        assertEquals("NAS", server.name)
        assertEquals("id-1", server.id)
    }

    @Test
    fun `rejects incomplete payloads`() {
        assertNull(DiscoveryResponseParser.parse("""{"Address":"http://10.0.0.5:8096"}"""))
        assertNull(DiscoveryResponseParser.parse("not-json"))
        assertNull(DiscoveryResponseParser.parse(""))
    }
}
