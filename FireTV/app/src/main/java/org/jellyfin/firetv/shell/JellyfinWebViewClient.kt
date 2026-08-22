package org.jellyfin.firetv.shell

import android.content.Context
import android.net.http.SslError
import android.webkit.CookieManager
import android.webkit.RenderProcessGoneDetail
import android.webkit.SslErrorHandler
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import org.jellyfin.firetv.core.DisplayScale
import org.jellyfin.firetv.core.NativeAsset
import org.jellyfin.firetv.core.NativeShellInjector
import org.jellyfin.firetv.core.ResourceKind
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
    private val ignoreSslErrors: Boolean,
    private val callbacks: Callbacks,
) : WebViewClient() {

    interface Callbacks {
        fun onPageReady()
        fun onPageFailed()
        fun onRendererCrashed()
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
        if (ResourceKind.isNativeBridge(path)) {
            return serveNativeAsset(request.url.lastPathSegment)
        }
        if (ResourceKind.isMedia(path)) {
            return null
        }
        if (request.method.equals("GET", ignoreCase = true) &&
            request.isForMainFrame &&
            ResourceKind.isWebDocument(path)
        ) {
            return injectNativeShell(request)
        }
        return null
    }

    override fun onPageFinished(view: WebView, url: String) {
        if (url.startsWith("about:", ignoreCase = true)) {
            return
        }
        view.evaluateJavascript(PAGE_READY_SCRIPT, null)
        callbacks.onPageReady()
    }

    override fun onReceivedError(view: WebView, request: WebResourceRequest, error: android.webkit.WebResourceError) {
        if (request.isForMainFrame) {
            callbacks.onPageFailed()
        }
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
        if (request.isForMainFrame && errorResponse.statusCode >= 500) {
            callbacks.onPageFailed()
        }
    }

    override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
        callbacks.onRendererCrashed()
        return true
    }

    private fun serveNativeAsset(name: String?): WebResourceResponse {
        if (name.isNullOrBlank()) {
            return notFound()
        }
        return try {
            val stream = context.assets.open("native/$name")
            WebResourceResponse(
                NativeAsset.mimeType(name),
                "utf-8",
                200,
                "OK",
                NativeAsset.responseHeaders(name),
                stream,
            )
        } catch (_: Exception) {
            notFound()
        }
    }

    private fun injectNativeShell(request: WebResourceRequest): WebResourceResponse? {
        return runCatching {
            val body = fetchBytes(request, request.url.toString()) ?: return null
            val html = NativeShellInjector.inject(body.toString(Charsets.UTF_8))
            WebResourceResponse(
                "text/html",
                "utf-8",
                200,
                "OK",
                mapOf("Content-Type" to "text/html; charset=utf-8", "Cache-Control" to "no-cache"),
                ByteArrayInputStream(html.toByteArray(Charsets.UTF_8)),
            )
        }.getOrNull()
    }

    private fun fetchBytes(request: WebResourceRequest, url: String): ByteArray? {
        val connection = URL(url).openConnection() as HttpURLConnection
        connection.connectTimeout = 8_000
        connection.readTimeout = 12_000
        connection.instanceFollowRedirects = true
        connection.requestMethod = "GET"
        request.requestHeaders.forEach { (key, value) ->
            if (!key.equals("Accept-Encoding", ignoreCase = true)) {
                connection.setRequestProperty(key, value)
            }
        }
        CookieManager.getInstance().getCookie(url)?.let { connection.setRequestProperty("Cookie", it) }
        if (ignoreSslErrors && connection is HttpsURLConnection) {
            trustAll(connection)
        }
        return try {
            val code = connection.responseCode
            if (code !in 200..299) {
                null
            } else {
                connection.inputStream.use { it.readBytes() }
            }
        } finally {
            connection.disconnect()
        }
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

    companion object {
        private val PAGE_READY_SCRIPT = """
            (function(){
              try { localStorage.setItem('layout','tv'); } catch(e) {}
              var active = document.activeElement;
              var typing = active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable);
              if (!typing && window.FireTvGuard) { window.FireTvGuard(); }
              var head = document.head;
              if (head) {
                var meta = document.querySelector('meta[name="viewport"]');
                if (!meta) { meta = document.createElement('meta'); meta.name='viewport'; head.insertBefore(meta, head.firstChild); }
                meta.setAttribute('content', '${DisplayScale.VIEWPORT_CONTENT}');
              }
            })();
        """.trimIndent()
    }
}
