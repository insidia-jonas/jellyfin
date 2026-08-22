package org.jellyfin.firetv.prefs

import android.content.Context
import android.os.Build
import android.provider.Settings
import java.util.UUID

class AppPreferences(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val appContext = context.applicationContext

    var serverUrl: String?
        get() = prefs.getString(KEY_SERVER_URL, null)
        set(value) {
            prefs.edit().putString(KEY_SERVER_URL, value).apply()
        }

    var ignoreSslErrors: Boolean
        get() = prefs.getBoolean(KEY_IGNORE_SSL, false)
        set(value) {
            prefs.edit().putBoolean(KEY_IGNORE_SSL, value).apply()
        }

    val deviceId: String
        get() {
            val existing = prefs.getString(KEY_DEVICE_ID, null)
            if (!existing.isNullOrBlank()) {
                return existing
            }
            val created = UUID.randomUUID().toString()
            prefs.edit().putString(KEY_DEVICE_ID, created).apply()
            return created
        }

    val deviceName: String
        get() {
            val named = runCatching {
                Settings.Global.getString(appContext.contentResolver, Settings.Global.DEVICE_NAME)
            }.getOrNull()
            val raw = named?.takeIf { it.isNotBlank() }
                ?: listOfNotNull(Build.MANUFACTURER, Build.MODEL).joinToString(" ").trim()
                .ifBlank { "Fire TV" }
            return raw.filter { it.isLetterOrDigit() || it == ' ' || it == '-' || it == '_' }
                .ifBlank { "Fire TV" }
        }

    fun clearServer() {
        prefs.edit().remove(KEY_SERVER_URL).apply()
    }

    companion object {
        private const val PREFS = "jellyfin_firetv"
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_IGNORE_SSL = "ignore_ssl"
        private const val KEY_DEVICE_ID = "device_id"
    }
}
