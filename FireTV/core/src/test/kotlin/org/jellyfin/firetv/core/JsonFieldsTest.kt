package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class JsonFieldsTest {
    @Test
    fun `decodes server unicode escapes in stream URLs and titles`() {
        assertEquals("/stream?a=1&b=2", jsonStringField("""{"url":"/stream?a=1\u0026b=2"}""", "url"))
        assertEquals("Größe 🎬", jsonStringField("""{"name":"Gr\u00f6\u00dfe \ud83c\udfac"}""", "name"))
    }

    @Test
    fun `does not decode escaped backslashes twice`() {
        assertEquals("literal\\n\\u0026", jsonStringField("""{"name":"literal\\n\\u0026"}""", "name"))
        assertEquals("a\r\n\t\"/b", jsonStringField("""{"name":"a\r\n\t\"\/b"}""", "name"))
    }
}
