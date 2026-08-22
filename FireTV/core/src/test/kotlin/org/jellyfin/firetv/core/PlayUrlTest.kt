package org.jellyfin.firetv.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class PlayUrlTest {
    @Test
    fun `prefers direct stream for mkv on exoplayer`() {
        val url = PlayUrl.resolve(
            "http://192.168.1.10:8096",
            MediaSourceUrls(
                transcodingUrl = "/videos/1/master.m3u8",
                directStreamUrl = "/Videos/1/stream.mkv?static=true",
                supportsDirectStream = true,
            ),
        )
        assertEquals("http://192.168.1.10:8096/Videos/1/stream.mkv?static=true", url)
    }

    @Test
    fun `falls back to hls transcode`() {
        val url = PlayUrl.resolve(
            "http://192.168.1.10:8096/",
            MediaSourceUrls(transcodingUrl = "videos/1/master.m3u8"),
        )
        assertEquals("http://192.168.1.10:8096/videos/1/master.m3u8", url)
    }

    @Test
    fun `keeps remote http paths for direct play`() {
        val url = PlayUrl.resolve(
            "http://192.168.1.10:8096",
            MediaSourceUrls(
                path = "https://cdn.example/movie.mp4",
                supportsDirectPlay = true,
            ),
        )
        assertEquals("https://cdn.example/movie.mp4", url)
    }
}
