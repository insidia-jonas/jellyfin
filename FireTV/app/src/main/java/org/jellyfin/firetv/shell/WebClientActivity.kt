package org.jellyfin.firetv.shell

import android.annotation.SuppressLint
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.view.KeyEvent
import android.view.View
import android.webkit.CookieManager
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.widget.Toast
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
import org.jellyfin.firetv.databinding.ItemDownloadBinding
import org.jellyfin.firetv.download.DownloadIndex
import org.jellyfin.firetv.download.FileDownloader
import org.jellyfin.firetv.player.NativePlayerBridge
import org.jellyfin.firetv.player.PlayerActivity
import org.jellyfin.firetv.prefs.AppPreferences
import org.json.JSONObject

class WebClientActivity : AppCompatActivity(), NativeInterface.Host {
    private lateinit var binding: ActivityWebClientBinding
    private lateinit var preferences: AppPreferences
    private lateinit var serverUrl: String
    private lateinit var mediaSession: PlaybackMediaSession
    private var ignoreSsl: Boolean = false
    private lateinit var nativeshellJs: String
    private var lastBackAt: Long = 0L
    private var initialWebFocusDone: Boolean = false
    private val refreshDownloads = object : Runnable {
        override fun run() {
            if (!::binding.isInitialized || !binding.downloadsOverlay.isVisible) {
                return
            }
            renderDownloads()
            binding.downloadsOverlay.postDelayed(this, 1_000)
        }
    }

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
        ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, preferences.ignoreSslErrors)
        nativeshellJs = runCatching {
            assets.open("native/nativeshell.js").bufferedReader().use { it.readText() }
        }.getOrDefault("")
        hideSystemBars()
        window.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        binding.retryButton.setOnClickListener { loadWebClient() }
        binding.errorChangeServerButton.setOnClickListener { openServerSelection() }
        binding.menuReload.setOnClickListener {
            hideOverlays()
            initialWebFocusDone = false
            binding.webView.reload()
        }
        binding.menuChangeServer.setOnClickListener { openServerSelection() }
        binding.menuDownloads.setOnClickListener {
            hideMenu()
            openDownloadManager()
        }
        binding.menuCloseDownloads.setOnClickListener { hideDownloads() }
        binding.menuExit.setOnClickListener { finishAffinity() }

        onBackPressedDispatcher.addCallback(
            this,
            object : OnBackPressedCallback(true) {
                override fun handleOnBackPressed() {
                    if (binding.downloadsOverlay.isVisible) {
                        hideDownloads()
                        return
                    }
                    if (binding.menuOverlay.isVisible) {
                        hideMenu()
                        return
                    }
                    handleWebBack()
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
        WebViewDisplayFit.apply(binding.webView)
        binding.webView.setLayerType(View.LAYER_TYPE_HARDWARE, null)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            binding.webView.setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, true)
            @Suppress("DEPRECATION")
            binding.webView.settings.safeBrowsingEnabled = false
        }

        binding.webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            javaScriptCanOpenWindowsAutomatically = false
            mediaPlaybackRequiresUserGesture = true
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            allowFileAccess = false
            allowContentAccess = false
            cacheMode = WebSettings.LOAD_DEFAULT
            offscreenPreRaster = true
            loadsImagesAutomatically = true
            blockNetworkImage = false
            userAgentString = "$userAgentString ${FireTvClient.APP_NAME.replace(" ", "")}/${FireTvClient.APP_VERSION}"
        }

        binding.webView.addJavascriptInterface(NativeInterface(this), "NativeInterface")
        binding.webView.addJavascriptInterface(NativePlayerBridge(this), "NativePlayer")
        binding.webView.webViewClient = JellyfinWebViewClient(
            context = this,
            ignoreSslErrors = ignoreSsl,
            callbacks = object : JellyfinWebViewClient.Callbacks {
                override fun onPageReady() {
                    runOnUiThread {
                        binding.loadingContainer.isVisible = false
                        binding.errorContainer.isVisible = false
                        if (!initialWebFocusDone) {
                            initialWebFocusDone = true
                            binding.webView.requestFocus()
                        }
                    }
                }

                override fun onPageFailed() {
                    runOnUiThread { showError() }
                }

                override fun onRendererCrashed() {
                    runOnUiThread {
                        Toast.makeText(this@WebClientActivity, R.string.webview_recovered, Toast.LENGTH_LONG).show()
                        recreate()
                    }
                }
            },
        )
        binding.webView.webChromeClient = object : WebChromeClient() {
            override fun onShowCustomView(view: View, callback: CustomViewCallback) {
                callback.onCustomViewHidden()
            }

            override fun onHideCustomView() = Unit
        }

        installDocumentStartScript()
    }

    private fun installDocumentStartScript() {
        if (!WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
            return
        }
        val origin = ServerUrl.origin(serverUrl)
        WebViewCompat.addDocumentStartJavaScript(binding.webView, nativeshellJs, setOf(origin))
    }

    private fun loadWebClient() {
        initialWebFocusDone = false
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
            if (binding.downloadsOverlay.isVisible) {
                hideDownloads()
            } else {
                toggleMenu()
            }
            return true
        }
        val script = RemoteKeyDispatcher.javascriptFor(event)
        if (script != null) {
            binding.webView.evaluateJavascript(script, null)
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    private fun handleWebBack() {
        binding.webView.evaluateJavascript(
            "window.FireTvRemote&&window.FireTvRemote.send('Escape');!!(window.FireTvCanExit&&window.FireTvCanExit())",
        ) { result ->
            if (result == "true") {
                val now = SystemClock.elapsedRealtime()
                if (now - lastBackAt < 2_200) {
                    finishAffinity()
                } else {
                    lastBackAt = now
                    Toast.makeText(this, R.string.press_back_again, Toast.LENGTH_SHORT).show()
                }
            }
        }
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

    private fun hideDownloads() {
        binding.downloadsOverlay.removeCallbacks(refreshDownloads)
        binding.downloadsOverlay.isVisible = false
        binding.webView.requestFocus()
    }

    private fun hideOverlays() {
        hideMenu()
        hideDownloads()
    }

    private fun renderDownloads() {
        val rows = DownloadIndex.list(this)
        binding.downloadsList.removeAllViews()
        binding.downloadsEmpty.isVisible = rows.isEmpty()
        rows.forEach { row ->
            val item = ItemDownloadBinding.inflate(layoutInflater, binding.downloadsList, false)
            item.downloadTitle.text = row.title
            item.downloadStatus.text = when (row.status) {
                DownloadIndex.Row.Status.RUNNING -> {
                    val pct = row.progressPercent.coerceAtLeast(0)
                    getString(R.string.download_status_running, pct)
                }
                DownloadIndex.Row.Status.PENDING -> getString(R.string.download_status_pending)
                DownloadIndex.Row.Status.PAUSED -> getString(R.string.download_status_paused)
                DownloadIndex.Row.Status.SUCCESS -> getString(R.string.download_status_success)
                DownloadIndex.Row.Status.FAILED -> getString(R.string.download_status_failed)
            }
            item.downloadProgress.isIndeterminate = row.progressPercent < 0 && row.status == DownloadIndex.Row.Status.RUNNING
            item.downloadProgress.progress = row.progressPercent.coerceAtLeast(0)
            binding.downloadsList.addView(item.root)
        }
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
                android.content.Intent(this, ConnectActivity::class.java).apply {
                    putExtra(ConnectActivity.EXTRA_CHANGE_SERVER, true)
                    flags = android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP or android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP
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
                startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url)))
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

    override fun launchPlayer(payload: String) {
        runOnUiThread {
            startActivity(
                android.content.Intent(this, PlayerActivity::class.java).apply {
                    putExtra(PlayerActivity.EXTRA_PAYLOAD, payload)
                    putExtra(PlayerActivity.EXTRA_IGNORE_SSL, ignoreSsl)
                },
            )
        }
    }

    override fun downloadFiles(json: String) {
        val result = FileDownloader.enqueue(this, json)
        val message = when {
            result.started > 0 -> resources.getQuantityString(R.plurals.download_started, result.started, result.started)
            result.duplicates > 0 -> getString(R.string.download_duplicate)
            else -> getString(R.string.download_failed)
        }
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
    }

    override fun openDownloadManager() {
        runOnUiThread {
            hideMenu()
            binding.downloadsOverlay.isVisible = true
            renderDownloads()
            binding.menuCloseDownloads.requestFocus()
            binding.downloadsOverlay.removeCallbacks(refreshDownloads)
            binding.downloadsOverlay.post(refreshDownloads)
        }
    }

    override fun runOnHost(block: () -> Unit) {
        runOnUiThread(block)
    }

    override fun onDestroy() {
        if (::binding.isInitialized) {
            binding.downloadsOverlay.removeCallbacks(refreshDownloads)
        }
        if (::mediaSession.isInitialized) {
            mediaSession.release()
        }
        if (::binding.isInitialized) {
            binding.webView.apply {
                loadUrl("about:blank")
                stopLoading()
                webChromeClient = null
                removeJavascriptInterface("NativeInterface")
                removeJavascriptInterface("NativePlayer")
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
