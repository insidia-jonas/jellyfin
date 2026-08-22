package org.jellyfin.firetv.core

import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

/**
 * Tiny authenticated HTTP helper for PlaybackInfo, subtitle search, and similar
 * Jellyfin REST calls. Callers must not run this on the main thread.
 */
object JellyfinHttp {
    data class Response(val code: Int, val body: String)

    fun get(
        url: String,
        accessToken: String,
        ignoreSslErrors: Boolean = false,
        deviceId: String = "",
        deviceName: String = "Fire TV",
        appName: String = FireTvClient.APP_NAME,
        appVersion: String = FireTvClient.APP_VERSION,
        connectTimeoutMs: Int = 12_000,
        readTimeoutMs: Int = 20_000,
    ): Response {
        return request(
            url = url,
            method = "GET",
            body = null,
            accessToken = accessToken,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            connectTimeoutMs = connectTimeoutMs,
            readTimeoutMs = readTimeoutMs,
        )
    }

    fun post(
        url: String,
        body: String,
        accessToken: String,
        ignoreSslErrors: Boolean = false,
        deviceId: String = "",
        deviceName: String = "Fire TV",
        appName: String = FireTvClient.APP_NAME,
        appVersion: String = FireTvClient.APP_VERSION,
        connectTimeoutMs: Int = 12_000,
        readTimeoutMs: Int = 20_000,
    ): Response {
        return request(
            url = url,
            method = "POST",
            body = body,
            accessToken = accessToken,
            ignoreSslErrors = ignoreSslErrors,
            deviceId = deviceId,
            deviceName = deviceName,
            appName = appName,
            appVersion = appVersion,
            connectTimeoutMs = connectTimeoutMs,
            readTimeoutMs = readTimeoutMs,
        )
    }

    fun request(
        url: String,
        method: String,
        body: String?,
        accessToken: String,
        ignoreSslErrors: Boolean,
        deviceId: String,
        deviceName: String,
        appName: String,
        appVersion: String,
        connectTimeoutMs: Int,
        readTimeoutMs: Int,
    ): Response {
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = connectTimeoutMs
            connection.readTimeout = readTimeoutMs
            connection.requestMethod = method
            connection.instanceFollowRedirects = true
            connection.setRequestProperty("Accept", "application/json")
            connection.setRequestProperty(
                "Authorization",
                authorization(appName, deviceName, deviceId, appVersion, accessToken),
            )
            if (accessToken.isNotBlank()) {
                connection.setRequestProperty("X-Emby-Token", accessToken)
            }
            if (ignoreSslErrors && connection is HttpsURLConnection) {
                trustAll(connection)
            }
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            val code = connection.responseCode
            val stream = if (code in 200..299) connection.inputStream else connection.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty()
            return Response(code, text)
        } finally {
            connection.disconnect()
        }
    }

    fun authorization(
        appName: String,
        deviceName: String,
        deviceId: String,
        appVersion: String,
        accessToken: String,
    ): String {
        val tokenPart = if (accessToken.isNotBlank()) ", Token=\"$accessToken\"" else ""
        return "MediaBrowser Client=\"$appName\", Device=\"$deviceName\", DeviceId=\"$deviceId\", Version=\"$appVersion\"$tokenPart"
    }

    fun trustAll(connection: HttpsURLConnection) {
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
