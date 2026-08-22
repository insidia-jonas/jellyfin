package org.jellyfin.firetv.shell

import android.content.Context
import android.net.http.SslError
import android.webkit.RenderProcessGoneDetail
import android.webkit.SslErrorHandler
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import org.jellyfin.firetv.core.DisplayScale
import org.jellyfin.firetv.core.ResourceKind
import java.io.ByteArrayInputStream

class JellyfinWebViewClient(
    private val context: Context,
    private val ignoreSslErrors: Boolean,
    private val nativeshellJs: String,
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
        // Never intercept media. Doing so (or re-downloading HTML on the WebView
        // thread) is what froze the app when a release/version was selected.
        if (ResourceKind.isMedia(path)) {
            return null
        }
        return null
    }

    override fun onPageStarted(view: WebView, url: String, favicon: android.graphics.Bitmap?) {
        view.evaluateJavascript(nativeshellJs, null)
    }

    override fun onPageFinished(view: WebView, url: String) {
        view.evaluateJavascript(nativeshellJs, null)
        view.evaluateJavascript(FIT_SCRIPT, null)
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
            val mime = if (name.endsWith(".js")) "application/javascript" else "application/octet-stream"
            WebResourceResponse(mime, "utf-8", stream)
        } catch (_: Exception) {
            notFound()
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

    companion object {
        private val FIT_SCRIPT = """
            (function(){
              try { localStorage.setItem('layout','tv'); } catch(e) {}
              var head = document.head;
              if (head) {
                var meta = document.querySelector('meta[name="viewport"]');
                if (!meta) { meta = document.createElement('meta'); meta.name='viewport'; head.insertBefore(meta, head.firstChild); }
                meta.setAttribute('content', '${DisplayScale.VIEWPORT_CONTENT}');
              }
              if (document.documentElement) {
                document.documentElement.style.width = '100%';
                document.documentElement.style.height = '100%';
              }
            })();
        """.trimIndent()
    }
}
