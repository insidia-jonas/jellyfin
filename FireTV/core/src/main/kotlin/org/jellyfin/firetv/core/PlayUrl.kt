package org.jellyfin.firetv.core

import java.net.URI

data class MediaSourceUrls(
    val transcodingUrl: String? = null,
    val directStreamUrl: String? = null,
    val path: String? = null,
    val supportsDirectPlay: Boolean = false,
    val supportsDirectStream: Boolean = false,
)

/**
 * Picks a playable HTTP URL from a Jellyfin MediaSource, preferring direct stream
 * (ExoPlayer can play MKV on Fire TV) and falling back to HLS transcoding.
 *
 * Live TV must never open the raw IPTV ingest host. After LiveStreams/Open the
 * server often returns `http://127.0.0.1:8096/LiveTv/LiveStreamFiles/…` — that
 * loopback address is rewritten onto the Fire TV's known server URL.
 */
object PlayUrl {
    fun resolve(serverBase: String, source: MediaSourceUrls): String? {
        val base = serverBase.trimEnd('/')
        val path = source.path
        if (source.supportsDirectPlay && path != null && isAbsoluteHttp(path) && !isLoopbackUrl(path)) {
            return path
        }
        if (source.supportsDirectStream && !source.directStreamUrl.isNullOrBlank()) {
            return bindToServer(base, source.directStreamUrl!!)
        }
        if (!source.transcodingUrl.isNullOrBlank()) {
            return bindToServer(base, source.transcodingUrl!!)
        }
        if (!path.isNullOrBlank() && isAbsoluteHttp(path) && !isLoopbackUrl(path)) {
            return path
        }
        return null
    }

    fun resolveLive(serverBase: String, source: MediaSourceUrls): String? {
        val base = serverBase.trimEnd('/')
        val candidates = liveCandidates(source)
        val proxy = candidates.firstNotNullOfOrNull { raw ->
            val bound = bindToServer(base, raw)
            bound?.takeIf { isLiveProxy(it) }
        }
        if (proxy != null) {
            return proxy
        }
        return candidates.firstNotNullOfOrNull { raw ->
            val bound = bindToServer(base, raw)
            bound?.takeIf { isSameOrigin(base, it) }
        }
    }

    /**
     * The `/LiveTv/LiveStreamFiles/…` proxy is a byte copy of the provider mux, so
     * German IPTV hands ExoPlayer mpeg2video + mp2 that Fire TV hardware cannot
     * decode — a black picture with no video size. Offer that raw path only when the
     * server allowed direct streaming; otherwise the transcoded URL is the one that
     * produces frames.
     */
    private fun liveCandidates(source: MediaSourceUrls): List<String> {
        val directAllowed = source.supportsDirectStream || source.supportsDirectPlay
        val transcoded = source.transcodingUrl?.takeIf { it.isNotBlank() }
        val direct = source.directStreamUrl?.takeIf { it.isNotBlank() }
        val raw = source.path?.takeIf { it.isNotBlank() }
        return buildList {
            if (directAllowed && direct != null) {
                add(direct)
            }
            if (transcoded != null) {
                add(transcoded)
            }
            if (raw != null && (directAllowed || transcoded == null)) {
                add(raw)
            }
        }
    }

    fun bindToServer(serverBase: String, url: String): String? {
        val trimmed = url.trim()
        if (trimmed.isBlank()) {
            return null
        }
        val base = serverBase.trimEnd('/')
        if (!isAbsoluteHttp(trimmed)) {
            return absolutize(base, trimmed)
        }
        if (isLoopbackUrl(trimmed) || isLiveProxy(trimmed)) {
            val uri = runCatching { URI(trimmed) }.getOrNull() ?: return absolutize(base, trimmed)
            val path = uri.rawPath.orEmpty().ifBlank { "/" }
            val query = uri.rawQuery?.takeIf { it.isNotBlank() }?.let { "?$it" }.orEmpty()
            return base + path + query
        }
        if (isSameOrigin(base, trimmed)) {
            return trimmed
        }
        return null
    }

    fun isLiveProxy(url: String): Boolean {
        val path = url.lowercase()
        return path.contains("/livetv/livestreamfiles/") ||
            path.contains("/livestreams/") ||
            (path.contains("/livetv/") && path.contains("/stream"))
    }

    fun isLoopbackUrl(url: String): Boolean {
        val host = runCatching { URI(url).host }.getOrNull()?.lowercase() ?: return false
        return host == "localhost" ||
            host == "127.0.0.1" ||
            host == "0.0.0.0" ||
            host == "::1" ||
            host == "[::1]"
    }

    fun isSameOrigin(serverBase: String, url: String): Boolean {
        val server = runCatching { URI(serverBase) }.getOrNull() ?: return false
        val other = runCatching { URI(url) }.getOrNull() ?: return false
        val serverHost = server.host?.lowercase() ?: return false
        val otherHost = other.host?.lowercase() ?: return false
        if (serverHost != otherHost) {
            return false
        }
        val serverPort = effectivePort(server)
        val otherPort = effectivePort(other)
        return serverPort == otherPort
    }

    fun absolutize(serverBase: String, url: String): String {
        val trimmed = url.trim()
        if (isAbsoluteHttp(trimmed)) {
            return trimmed
        }
        val base = serverBase.trimEnd('/')
        return if (trimmed.startsWith("/")) base + trimmed else "$base/$trimmed"
    }

    fun isAbsoluteHttp(url: String): Boolean {
        return url.startsWith("http://", ignoreCase = true) || url.startsWith("https://", ignoreCase = true)
    }

    private fun effectivePort(uri: URI): Int {
        if (uri.port != -1) {
            return uri.port
        }
        return if (uri.scheme.equals("https", ignoreCase = true)) 443 else 80
    }
}
