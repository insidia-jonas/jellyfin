package org.jellyfin.firetv.core

/**
 * Shapes UDP discovery results the way jellyfin-web's connectionManager
 * expects from NativeShell.findServers().
 */
object NativeServerList {
    fun toNativeShellJson(servers: List<DiscoveredServer>): String {
        return servers.joinToString(prefix = "[", postfix = "]") { server ->
            buildString {
                append('{')
                append("\"name\":").append(jsonEscape(server.name)).append(',')
                append("\"Name\":").append(jsonEscape(server.name)).append(',')
                append("\"id\":").append(jsonEscape(server.id)).append(',')
                append("\"Id\":").append(jsonEscape(server.id)).append(',')
                append("\"address\":").append(jsonEscape(server.address)).append(',')
                append("\"Address\":").append(jsonEscape(server.address)).append(',')
                append("\"endpointAddress\":").append(jsonEscape(server.address))
                append('}')
            }
        }
    }

    fun discoverJson(timeoutMs: Int): String {
        val servers = runCatching {
            UdpServerDiscovery().discover(timeoutMs.coerceIn(500, 8_000))
        }.getOrDefault(emptyList())
        return toNativeShellJson(servers)
    }
}
