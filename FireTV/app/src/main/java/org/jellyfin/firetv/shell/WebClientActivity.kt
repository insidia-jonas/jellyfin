package org.jellyfin.firetv.shell

import android.annotation.SuppressLint
import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.widget.FrameLayout
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.jellyfin.firetv.BuildConfig
import org.jellyfin.firetv.R
import org.jellyfin.firetv.connect.ConnectActivity
import org.jellyfin.firetv.core.FireTvClient
import org.jellyfin.firetv.core.ServerUrl
import org.jellyfin.firetv.databinding.ActivityWebClientBinding
import org.jellyfin.firetv.prefs.AppPreferences
import org.json.JSONObject

class WebClientActivity : AppCompatActivity(), NativeInterface.Host {
    private lateinit var binding: ActivityWebClientBinding
    private lateinit var preferences: AppPreferences
    private lateinit var serverUrl: String
    private lateinit var mediaSession: PlaybackMediaSession
    private var customView: View? = null
    private var customViewCallback: WebChromeClient.CustomViewCallback? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        preferences = AppPreferences(this)
        serverUrl = intent.getStringExtra(EXTRA_SERVER_URL) ?: preferences.serverUrl.orEmpty()
        if (serverUrl.isBlank()) {
            openServerSelection()
            return
        }

        binding = ActivityWebClientBinding.inflate(layoutInflater)
        setContentView(binding.root)
        mediaSession = PlaybackMediaSession(this)
        hideSystemBars()
        window.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        binding.retryButton.setOnClickListener { loadWebClient() }
        binding.errorChangeServerButton.setOnClickListener { openServerSelection() }
        binding.menuReload.setOnClickListener {
            hideMenu()
            binding.webView.reload()
        }
        binding.menuChangeServer.setOnClickListener { openServerSelection() }
        binding.menuExit.setOnClickListener { finishAffinity() }

        onBackPressedDispatcher.addCallback(
            this,
            object : OnBackPressedCallback(true) {
                override fun handleOnBackPressed() {
                    if (binding.menuOverlay.isVisible) {
                        hideMenu()
                        return
                    }
                    if (customView != null) {
                        binding.webView.webChromeClient?.onHideCustomView()
                        return
                    }
                    binding.webView.evaluateJavascript(
                        "window.FireTvRemote&&window.FireTvRemote.send('Escape')",
                        null,
                    )
                }
            },
        )

        configureWebView()
        loadWebClient()
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun configureWebView() {
        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(binding.webView, true)

        binding.webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            javaScriptCanOpenWindowsAutomatically = true
            mediaPlaybackRequiresUserGesture = false
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            useWideViewPort = true
            loadWithOverviewMode = true
            allowFileAccess = false
            allowContentAccess = false
            cacheMode = WebSettings.LOAD_DEFAULT
            userAgentString = "$userAgentString ${FireTvClient.APP_NAME.replace(" ", "")}/${FireTvClient.APP_VERSION}"
        }

        binding.webView.addJavascriptInterface(NativeInterface(this), "NativeInterface")
        binding.webView.webViewClient = JellyfinWebViewClient(
            context = this,
            userAgent = binding.webView.settings.userAgentString,
            ignoreSslErrors = intent.getBooleanExtra(EXTRA_IGNORE_SSL, preferences.ignoreSslErrors),
            callbacks = object : JellyfinWebViewClient.Callbacks {
                override fun onPageReady() {
                    runOnUiThread {
                        binding.loadingContainer.isVisible = false
                        binding.errorContainer.isVisible = false
                        binding.webView.requestFocus()
                    }
                }

                override fun onPageFailed() {
                    runOnUiThread { showError() }
                }
            },
        )
        binding.webView.webChromeClient = object : WebChromeClient() {
            override fun onShowCustomView(view: View, callback: CustomViewCallback) {
                if (customView != null) {
                    callback.onCustomViewHidden()
                    return
                }
                customView = view
                customViewCallback = callback
                binding.webView.visibility = View.GONE
                (binding.root as FrameLayout).addView(
                    view,
                    0,
                    FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT),
                )
            }

            override fun onHideCustomView() {
                val view = customView ?: return
                (binding.root as FrameLayout).removeView(view)
                customView = null
                customViewCallback?.onCustomViewHidden()
                customViewCallback = null
                binding.webView.visibility = View.VISIBLE
            }
        }

        installDocumentStartScript()
    }

    private fun installDocumentStartScript() {
        if (!WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
            return
        }
        val script = runCatching {
            assets.open("native/nativeshell.js").bufferedReader().use { it.readText() }
        }.getOrNull() ?: return
        val origin = ServerUrl.origin(serverUrl)
        WebViewCompat.addDocumentStartJavaScript(binding.webView, script, setOf(origin))
    }

    private fun loadWebClient() {
        binding.errorContainer.isVisible = false
        binding.loadingContainer.isVisible = true
        val target = serverUrl.trimEnd('/') + "/web/"
        binding.webView.loadUrl(target)
    }

    private fun showError() {
        binding.loadingContainer.isVisible = false
        binding.errorContainer.isVisible = true
        binding.retryButton.requestFocus()
    }

    private fun hideSystemBars() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_DOWN && event.keyCode == KeyEvent.KEYCODE_MENU) {
            toggleMenu()
            return true
        }
        val script = RemoteKeyDispatcher.javascriptFor(event)
        if (script != null) {
            binding.webView.evaluateJavascript(script, null)
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    private fun toggleMenu() {
        binding.menuOverlay.isVisible = !binding.menuOverlay.isVisible
        if (binding.menuOverlay.isVisible) {
            binding.menuReload.requestFocus()
        } else {
            binding.webView.requestFocus()
        }
    }

    private fun hideMenu() {
        binding.menuOverlay.isVisible = false
        binding.webView.requestFocus()
    }

    override fun deviceInformation(): JSONObject {
        return JSONObject()
            .put("deviceId", preferences.deviceId)
            .put("deviceName", preferences.deviceName)
            .put("appName", FireTvClient.APP_NAME)
            .put("appVersion", BuildConfig.VERSION_NAME)
    }

    override fun exitApp() {
        runOnUiThread { finishAffinity() }
    }

    override fun openServerSelection() {
        runOnUiThread {
            startActivity(
                Intent(this, ConnectActivity::class.java).apply {
                    putExtra(ConnectActivity.EXTRA_CHANGE_SERVER, true)
                    flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
                },
            )
            finish()
        }
    }

    override fun openClientSettings() {
        runOnUiThread { toggleMenu() }
    }

    override fun openUrl(url: String) {
        runOnUiThread {
            runCatching {
                startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url)))
            }
        }
    }

    override fun updateMediaSession(json: String) {
        mediaSession.update(json)
    }

    override fun hideMediaSession() {
        mediaSession.hide()
    }

    override fun enableFullscreen() {
        runOnUiThread { hideSystemBars() }
    }

    override fun disableFullscreen() {
        runOnUiThread { hideSystemBars() }
    }

    override fun updateVolumeLevel(level: Int) = Unit

    override fun onDestroy() {
        if (::mediaSession.isInitialized) {
            mediaSession.release()
        }
        if (::binding.isInitialized) {
            binding.webView.apply {
                loadUrl("about:blank")
                stopLoading()
                webChromeClient = null
                removeJavascriptInterface("NativeInterface")
                destroy()
            }
        }
        super.onDestroy()
    }

    companion object {
        const val EXTRA_SERVER_URL = "server_url"
        const val EXTRA_IGNORE_SSL = "ignore_ssl"
        const val EXTRA_SERVER_NAME = "server_name"
    }
}
