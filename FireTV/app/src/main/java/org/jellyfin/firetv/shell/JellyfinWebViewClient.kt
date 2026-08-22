package org.jellyfin.firetv.shell

import android.content.Context
import android.net.Uri
import android.net.http.SslError
import android.webkit.CookieManager
import android.webkit.SslErrorHandler
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import org.jellyfin.firetv.core.NativeShellInjector
import java.io.ByteArrayInputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

class JellyfinWebViewClient(
    private val context: Context,
    private val userAgent: String,
    private val ignoreSslErrors: Boolean,
    private val callbacks: Callbacks,
) : WebViewClient() {

    interface Callbacks {
        fun onPageReady()
        fun onPageFailed()
    }

    override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
        val url = request.url.toString()
        val allowed = url.startsWith("http://") ||
            url.startsWith("https://") ||
            url.startsWith("about:") ||
            url.startsWith("blob:")
        return !allowed
    }

    override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
        val path = request.url.path.orEmpty()
        if (path.contains("/native/")) {
            return serveNativeAsset(request.url)
        }
        if (request.isForMainFrame && request.method.equals("GET", ignoreCase = true) && looksLikeDocument(request.url)) {
            return fetchAndInject(request.url.toString())
        }
        return null
    }

    override fun onPageFinished(view: WebView, url: String) {
        callbacks.onPageReady()
    }

    override fun onReceivedError(view: WebView, request: WebResourceRequest, error: android.webkit.WebResourceError) {
        if (request.isForMainFrame) {
            callbacks.onPageFailed()
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onReceivedError(view: WebView, errorCode: Int, description: String, failingUrl: String) {
        callbacks.onPageFailed()
    }

    override fun onReceivedSslError(view: WebView, handler: SslErrorHandler, error: SslError) {
        if (ignoreSslErrors) {
            handler.proceed()
        } else {
            handler.cancel()
            callbacks.onPageFailed()
        }
    }

    override fun onReceivedHttpError(view: WebView, request: WebResourceRequest, errorResponse: WebResourceResponse) {
        if (request.isForMainFrame && errorResponse.statusCode >= 400) {
            callbacks.onPageFailed()
        }
    }

    private fun looksLikeDocument(uri: Uri): Boolean {
        val path = uri.path.orEmpty().lowercase()
        val blocked = listOf(".js", ".css", ".map", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".woff", ".woff2", ".ttf", ".m3u8", ".mp4", ".mp3", ".ts", ".m4s")
        if (blocked.any { path.endsWith(it) }) {
            return false
        }
        if (path.contains("/videos/") || path.contains("/audio/") || (path.contains("/items/") && path.contains("/download"))) {
            return false
        }
        return true
    }

    private fun serveNativeAsset(uri: Uri): WebResourceResponse {
        val name = uri.lastPathSegment ?: return notFound()
        return try {
            val stream = context.assets.open("native/$name")
            val mime = if (name.endsWith(".js")) "application/javascript" else "application/octet-stream"
            WebResourceResponse(mime, "utf-8", stream)
        } catch (_: Exception) {
            notFound()
        }
    }

    private fun fetchAndInject(url: String): WebResourceResponse? {
        return try {
            val html = downloadHtml(url) ?: return null
            val injected = NativeShellInjector.inject(html)
            val bytes = injected.toByteArray(Charsets.UTF_8)
            WebResourceResponse(
                "text/html",
                "utf-8",
                200,
                "OK",
                mapOf("Content-Type" to "text/html; charset=utf-8"),
                ByteArrayInputStream(bytes),
            )
        } catch (_: Exception) {
            null
        }
    }

    private fun downloadHtml(startUrl: String): String? {
        var current = startUrl
        repeat(5) {
            val connection = URL(current).openConnection() as HttpURLConnection
            connection.instanceFollowRedirects = false
            connection.connectTimeout = 15_000
            connection.readTimeout = 15_000
            connection.requestMethod = "GET"
            connection.setRequestProperty("User-Agent", userAgent)
            connection.setRequestProperty("Accept", "text/html,application/xhtml+xml")
            connection.setRequestProperty("Accept-Encoding", "identity")
            CookieManager.getInstance().getCookie(current)?.let { connection.setRequestProperty("Cookie", it) }
            if (ignoreSslErrors && connection is HttpsURLConnection) {
                trustAll(connection)
            }
            val code = connection.responseCode
            storeCookies(current, connection)
            val location = connection.getHeaderField("Location")
            if (code in 300..399 && !location.isNullOrBlank()) {
                current = URL(URL(current), location).toString()
                connection.disconnect()
                return@repeat
            }
            val mime = connection.contentType.orEmpty()
            if (code !in 200..299 || (mime.isNotEmpty() && "html" !in mime.lowercase() && "xml" !in mime.lowercase())) {
                connection.disconnect()
                return null
            }
            val body = connection.inputStream.bufferedReader().use { it.readText() }
            connection.disconnect()
            return body
        }
        return null
    }

    private fun storeCookies(url: String, connection: HttpURLConnection) {
        val manager = CookieManager.getInstance()
        connection.headerFields["Set-Cookie"]?.forEach { cookie ->
            manager.setCookie(url, cookie)
        }
        connection.headerFields["set-cookie"]?.forEach { cookie ->
            manager.setCookie(url, cookie)
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

    private fun notFound(): WebResourceResponse {
        return WebResourceResponse(
            "text/plain",
            "utf-8",
            404,
            "Not Found",
            emptyMap(),
            ByteArrayInputStream(ByteArray(0)),
        )
    }
}
