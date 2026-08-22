package org.jellyfin.firetv.download

import android.app.DownloadManager
import android.content.Context
import android.database.Cursor

object DownloadIndex {
    data class Row(
        val id: Long,
        val title: String,
        val status: Status,
        val bytes: Long,
        val total: Long,
    ) {
        enum class Status { PENDING, RUNNING, PAUSED, SUCCESS, FAILED }

        val progressPercent: Int
            get() = if (total > 0) ((bytes * 100) / total).toInt().coerceIn(0, 100) else -1
    }

    private const val PREFS = "jellyfin_firetv_downloads"
    private const val KEY_IDS = "ids"

    fun remember(context: Context, id: Long) {
        val ids = ids(context).toMutableSet()
        ids.add(id)
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putStringSet(KEY_IDS, ids.map { it.toString() }.toSet())
            .apply()
    }

    fun ids(context: Context): Set<Long> {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getStringSet(KEY_IDS, emptySet())
            .orEmpty()
            .mapNotNull { it.toLongOrNull() }
            .toSet()
    }

    fun list(context: Context): List<Row> {
        val remembered = ids(context)
        if (remembered.isEmpty()) {
            return emptyList()
        }
        val manager = context.getSystemService(Context.DOWNLOAD_SERVICE) as? DownloadManager
            ?: return emptyList()
        val query = DownloadManager.Query()
        val rows = mutableListOf<Row>()
        val cursor = runCatching { manager.query(query) }.getOrNull() ?: return emptyList()
        cursor.use {
            val idIdx = it.getColumnIndex(DownloadManager.COLUMN_ID)
            val titleIdx = it.getColumnIndex(DownloadManager.COLUMN_TITLE)
            val statusIdx = it.getColumnIndex(DownloadManager.COLUMN_STATUS)
            val bytesIdx = it.getColumnIndex(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)
            val totalIdx = it.getColumnIndex(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)
            while (it.moveToNext()) {
                if (idIdx < 0 || statusIdx < 0) {
                    continue
                }
                val id = it.getLong(idIdx)
                if (id !in remembered) {
                    continue
                }
                rows += Row(
                    id = id,
                    title = it.safeString(titleIdx).ifBlank { "Jellyfin" },
                    status = mapStatus(it.getInt(statusIdx)),
                    bytes = if (bytesIdx >= 0) it.getLong(bytesIdx).coerceAtLeast(0) else 0,
                    total = if (totalIdx >= 0) it.getLong(totalIdx) else -1,
                )
            }
        }
        return rows.sortedByDescending { it.id }
    }

    private fun mapStatus(status: Int): Row.Status {
        return when (status) {
            DownloadManager.STATUS_PENDING -> Row.Status.PENDING
            DownloadManager.STATUS_RUNNING -> Row.Status.RUNNING
            DownloadManager.STATUS_PAUSED -> Row.Status.PAUSED
            DownloadManager.STATUS_SUCCESSFUL -> Row.Status.SUCCESS
            else -> Row.Status.FAILED
        }
    }

    private fun Cursor.safeString(index: Int): String {
        if (index < 0 || isNull(index)) {
            return ""
        }
        return getString(index).orEmpty()
    }
}
