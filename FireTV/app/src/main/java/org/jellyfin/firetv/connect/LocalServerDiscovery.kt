package org.jellyfin.firetv.connect

import android.content.Context
import android.net.wifi.WifiManager
import org.jellyfin.firetv.core.DiscoveredServer
import org.jellyfin.firetv.core.UdpServerDiscovery
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder

object LocalServerDiscovery {
    fun findServers(context: Context): List<DiscoveredServer> {
        val discovery = UdpServerDiscovery(broadcastAddresses(context))
        return discovery.discover()
    }

    @Suppress("DEPRECATION")
    private fun broadcastAddresses(context: Context): List<InetAddress> {
        val addresses = mutableListOf(InetAddress.getByName("255.255.255.255"))
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val dhcp = wifi?.dhcpInfo
        if (dhcp != null && dhcp.ipAddress != 0 && dhcp.netmask != 0) {
            val broadcast = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
            val bytes = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(broadcast).array()
            runCatching { addresses.add(InetAddress.getByAddress(bytes)) }
        }
        return addresses.distinct()
    }
}
