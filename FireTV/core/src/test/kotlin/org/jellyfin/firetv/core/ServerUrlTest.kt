package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class ServerUrlTest {
    @Test
    fun `bare ipv4 uses default jellyfin port as first candidate`() {
        assertEquals(
            listOf("http://192.168.1.10:8096", "http://192.168.1.10"),
            ServerUrl.candidates("192.168.1.10"),
        )
    }

    @Test
    fun `explicit port is kept`() {
        assertEquals(
            listOf("http://192.168.1.10:8096"),
            ServerUrl.candidates("192.168.1.10:8096"),
        )
    }

    @Test
    fun `https domain is not given a default port`() {
        assertEquals(
            listOf("https://jellyfin.example.com"),
            ServerUrl.candidates("https://jellyfin.example.com"),
        )
    }

    @Test
    fun `web path suffix is stripped`() {
        assertEquals("http://192.168.0.2:8096", ServerUrl.normalize("http://192.168.0.2:8096/web/"))
        assertEquals("http://192.168.0.2:8096", ServerUrl.normalize("http://192.168.0.2:8096/web/index.html"))
    }

    @Test
    fun `scheme is required after normalize`() {
        assertNull(ServerUrl.normalize(""))
        assertNull(ServerUrl.normalize("ftp://media.local"))
        assertNull(ServerUrl.normalize("://missing-host"))
    }

    @Test
    fun `public info path is appended`() {
        assertEquals(
            "http://192.168.1.10:8096/System/Info/Public",
            ServerUrl.publicInfoUrl("http://192.168.1.10:8096/"),
        )
    }

    @Test
    fun `origin drops path`() {
        assertEquals("http://192.168.1.10:8096", ServerUrl.origin("http://192.168.1.10:8096/jellyfin"))
    }

    @Test
    fun `local hostname assumes default port`() {
        assertEquals(
            listOf("http://jellyfin:8096", "http://jellyfin"),
            ServerUrl.candidates("jellyfin"),
        )
    }
}
