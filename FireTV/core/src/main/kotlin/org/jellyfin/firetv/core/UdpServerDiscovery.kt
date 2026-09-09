package org.jellyfin.firetv.core

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

/**
 * UDP auto-discovery for Jellyfin servers on the local network (port 7359).
 */
class UdpServerDiscovery(
    private val broadcastAddresses: List<InetAddress> = listOf(InetAddress.getByName("255.255.255.255")),
    private val port: Int = FireTvClient.DISCOVERY_PORT,
    private val message: String = FireTvClient.DISCOVERY_MESSAGE,
) {
    fun discover(timeoutMs: Int = 3_000): List<DiscoveredServer> {
        val found = linkedMapOf<String, DiscoveredServer>()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 500
            val payload = message.toByteArray(Charsets.UTF_8)
            for (address in broadcastAddresses) {
                runCatching {
                    socket.send(DatagramPacket(payload, payload.size, InetSocketAddress(address, port)))
                }
            }

            val deadline = System.currentTimeMillis() + timeoutMs
            val buffer = ByteArray(4_096)
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val body = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                val server = DiscoveryResponseParser.parse(body) ?: continue
                found.putIfAbsent(server.id, server)
            }
        }
        return found.values.toList()
    }
}
