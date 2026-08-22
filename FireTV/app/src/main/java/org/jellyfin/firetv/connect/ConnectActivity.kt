package org.jellyfin.firetv.connect

import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.inputmethod.EditorInfo
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.jellyfin.firetv.R
import org.jellyfin.firetv.core.DiscoveredServer
import org.jellyfin.firetv.databinding.ActivityConnectBinding
import org.jellyfin.firetv.databinding.ItemServerBinding
import org.jellyfin.firetv.prefs.AppPreferences
import org.jellyfin.firetv.shell.WebClientActivity

class ConnectActivity : AppCompatActivity() {
    private lateinit var binding: ActivityConnectBinding
    private lateinit var preferences: AppPreferences

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        preferences = AppPreferences(this)
        binding = ActivityConnectBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.ignoreSsl.isChecked = preferences.ignoreSslErrors
        binding.urlInput.setText(preferences.serverUrl.orEmpty())

        binding.connectButton.setOnClickListener { connect(binding.urlInput.text?.toString().orEmpty()) }
        binding.discoverButton.setOnClickListener { discover() }
        binding.urlInput.setOnEditorActionListener { _, actionId, event ->
            val go = actionId == EditorInfo.IME_ACTION_GO ||
                (event?.keyCode == KeyEvent.KEYCODE_ENTER && event.action == KeyEvent.ACTION_DOWN)
            if (go) {
                connect(binding.urlInput.text?.toString().orEmpty())
                true
            } else {
                false
            }
        }

        val forcePicker = intent.getBooleanExtra(EXTRA_CHANGE_SERVER, false)
        val saved = preferences.serverUrl
        if (!forcePicker && !saved.isNullOrBlank()) {
            connect(saved, auto = true)
        } else {
            binding.urlInput.requestFocus()
        }
    }

    private fun connect(raw: String, auto: Boolean = false) {
        setBusy(true, getString(R.string.connecting))
        lifecycleScope.launch {
            val result = withContext(Dispatchers.IO) {
                ServerReachability.findReachable(raw, binding.ignoreSsl.isChecked)
            }
            if (result == null) {
                setBusy(false, getString(if (raw.isBlank()) R.string.invalid_url else R.string.connection_failed))
                if (!auto) {
                    Toast.makeText(this@ConnectActivity, R.string.connection_failed, Toast.LENGTH_LONG).show()
                }
                binding.urlInput.requestFocus()
                return@launch
            }
            val (url, info) = result
            preferences.serverUrl = url
            preferences.ignoreSslErrors = binding.ignoreSsl.isChecked
            setBusy(false, getString(R.string.connected_as, info.serverName ?: url))
            startActivity(
                Intent(this@ConnectActivity, WebClientActivity::class.java).apply {
                    putExtra(WebClientActivity.EXTRA_SERVER_URL, url)
                    putExtra(WebClientActivity.EXTRA_IGNORE_SSL, binding.ignoreSsl.isChecked)
                    putExtra(WebClientActivity.EXTRA_SERVER_NAME, info.serverName)
                },
            )
            finish()
        }
    }

    private fun discover() {
        setBusy(true, getString(R.string.discovering))
        binding.serverList.removeAllViews()
        binding.foundLabel.isVisible = false
        lifecycleScope.launch {
            val servers = withContext(Dispatchers.IO) {
                runCatching { LocalServerDiscovery.findServers(this@ConnectActivity) }.getOrDefault(emptyList())
            }
            setBusy(false, if (servers.isEmpty()) getString(R.string.no_servers_found) else null)
            renderServers(servers)
        }
    }

    private fun renderServers(servers: List<DiscoveredServer>) {
        binding.foundLabel.isVisible = servers.isNotEmpty()
        binding.serverList.removeAllViews()
        val inflater = LayoutInflater.from(this)
        servers.forEach { server ->
            val item = ItemServerBinding.inflate(inflater, binding.serverList, false)
            item.serverName.text = server.name
            item.serverAddress.text = server.address
            item.root.setOnClickListener {
                binding.urlInput.setText(server.address)
                connect(server.address)
            }
            binding.serverList.addView(item.root)
        }
        if (servers.isNotEmpty()) {
            binding.serverList.getChildAt(0)?.requestFocus()
        }
    }

    private fun setBusy(busy: Boolean, status: String?) {
        binding.loading.isVisible = busy
        binding.connectButton.isEnabled = !busy
        binding.discoverButton.isEnabled = !busy
        binding.statusText.isVisible = !status.isNullOrBlank()
        binding.statusText.text = status
        binding.statusText.setTextColor(
            getColor(if (status == getString(R.string.connection_failed) || status == getString(R.string.invalid_url) || status == getString(R.string.no_servers_found)) {
                R.color.error
            } else {
                R.color.text_secondary
            }),
        )
    }

    companion object {
        const val EXTRA_CHANGE_SERVER = "change_server"
    }
}
