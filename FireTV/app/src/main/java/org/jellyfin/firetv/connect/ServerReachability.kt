package org.jellyfin.firetv.connect

import org.jellyfin.firetv.core.PublicServerInfo
import org.jellyfin.firetv.core.PublicServerInfoParser
import org.jellyfin.firetv.core.ServerUrl
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

object ServerReachability {
    fun findReachable(rawUrl: String, ignoreSslErrors: Boolean): Pair<String, PublicServerInfo>? {
        for (candidate in ServerUrl.candidates(rawUrl)) {
            val info = probe(candidate, ignoreSslErrors) ?: continue
            return candidate to info
        }
        return null
    }

    fun probe(serverUrl: String, ignoreSslErrors: Boolean): PublicServerInfo? {
        val connection = open(ServerUrl.publicInfoUrl(serverUrl), ignoreSslErrors) ?: return null
        return try {
            connection.connectTimeout = 8_000
            connection.readTimeout = 8_000
            connection.instanceFollowRedirects = true
            connection.requestMethod = "GET"
            connection.setRequestProperty("Accept", "application/json")
            val code = connection.responseCode
            if (code !in 200..299) {
                return null
            }
            val body = connection.inputStream.bufferedReader().use { it.readText() }
            PublicServerInfoParser.parse(body)
        } catch (_: Exception) {
            null
        } finally {
            connection.disconnect()
        }
    }

    private fun open(url: String, ignoreSslErrors: Boolean): HttpURLConnection? {
        return try {
            val connection = URL(url).openConnection() as HttpURLConnection
            if (ignoreSslErrors && connection is HttpsURLConnection) {
                trustAll(connection)
            }
            connection
        } catch (_: Exception) {
            null
        }
    }

    private fun trustAll(connection: HttpsURLConnection) {
        val trustManager = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) = Unit
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(trustManager), SecureRandom())
        connection.sslSocketFactory = context.socketFactory
        connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
    }
}
