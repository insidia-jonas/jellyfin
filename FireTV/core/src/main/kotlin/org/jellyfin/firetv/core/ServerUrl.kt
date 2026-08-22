package org.jellyfin.firetv.core

import java.net.URI

/**
 * Normalizes user-entered Jellyfin server addresses and expands connection candidates.
 *
 * Fire TV users often type a bare LAN IP. In that case the default HTTP port 8096 is added.
 * Hosted domains and URLs that already include a port or scheme are left intact.
 */
object ServerUrl {
    private val IPV4 = Regex("""^\d{1,3}(?:\.\d{1,3}){3}$""")

    fun normalize(raw: String): String? {
        val trimmed = raw.trim()
        if (trimmed.isEmpty()) {
            return null
        }

        val withScheme = if ("://" in trimmed) trimmed else "http://$trimmed"
        val uri = runCatching { URI(withScheme) }.getOrNull() ?: return null
        val scheme = uri.scheme?.lowercase() ?: return null
        if (scheme != "http" && scheme != "https") {
            return null
        }
        val host = uri.host?.takeIf { it.isNotBlank() } ?: return null

        val portPart = if (uri.port != -1) ":${uri.port}" else ""
        val path = uri.path.orEmpty()
            .trimEnd('/')
            .removeSuffix("/web")
            .removeSuffix("/web/index.html")
            .trimEnd('/')

        return buildString {
            append(scheme).append("://").append(host).append(portPart)
            if (path.isNotEmpty()) {
                append(path)
            }
        }
    }

    /**
     * Returns unique candidate base URLs to try, in preference order.
     */
    fun candidates(raw: String): List<String> {
        val normalized = normalize(raw) ?: return emptyList()
        val uri = URI(normalized)
        if (uri.port == -1 && uri.scheme == "http" && shouldAssumeDefaultPort(uri.host)) {
            return listOf("${uri.scheme}://${uri.host}:${FireTvClient.DEFAULT_HTTP_PORT}", normalized)
        }
        return listOf(normalized)
    }

    fun publicInfoUrl(serverUrl: String): String {
        return serverUrl.trimEnd('/') + FireTvClient.PUBLIC_INFO_PATH
    }

    fun origin(serverUrl: String): String {
        val uri = URI(serverUrl)
        val portPart = if (uri.port != -1) ":${uri.port}" else ""
        return "${uri.scheme}://${uri.host}$portPart"
    }

    private fun shouldAssumeDefaultPort(host: String): Boolean {
        if (IPV4.matches(host)) {
            return true
        }
        if (host.equals("localhost", ignoreCase = true) || host.endsWith(".local", ignoreCase = true)) {
            return true
        }
        // Single-label LAN hostnames (e.g. "jellyfin") typically speak on 8096.
        return "." !in host
    }
}
