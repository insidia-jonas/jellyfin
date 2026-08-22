package org.jellyfin.firetv.download

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.webkit.CookieManager
import org.jellyfin.firetv.core.DownloadRequests
import org.jellyfin.firetv.core.StreamAuth

object FileDownloader {
    fun enqueue(context: Context, json: String, accessToken: String? = null): Int {
        val files = DownloadRequests.parse(json)
        if (files.isEmpty()) {
            return 0
        }
        val manager = context.getSystemService(Context.DOWNLOAD_SERVICE) as? DownloadManager
            ?: return 0
        var started = 0
        for (file in files) {
            if (enqueueOne(context, manager, file, accessToken)) {
                started++
            }
        }
        return started
    }

    private fun enqueueOne(
        context: Context,
        manager: DownloadManager,
        file: DownloadRequests.File,
        accessToken: String?,
    ): Boolean {
        val url = StreamAuth.withAccessToken(file.url, accessToken.orEmpty())
        val name = DownloadRequests.safeFilename(
            file.filename ?: file.title,
            fallback = file.itemId?.let { "jellyfin-$it" } ?: "jellyfin-download",
        )
        val request = DownloadManager.Request(Uri.parse(url)).apply {
            setTitle(file.title?.ifBlank { null } ?: name)
            setDescription(name)
            setMimeType(guessMime(name))
            setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            setAllowedOverMetered(true)
            setAllowedOverRoaming(true)
            addRequestHeader("User-Agent", "JellyfinFireTV")
            val cookie = CookieManager.getInstance().getCookie(url)
            if (!cookie.isNullOrBlank()) {
                addRequestHeader("Cookie", cookie)
            }
            if (!accessToken.isNullOrBlank()) {
                addRequestHeader("X-Emby-Token", accessToken)
            }
        }
        val publicOk = runCatching {
            request.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, "Jellyfin/$name")
            manager.enqueue(request)
        }.isSuccess
        if (publicOk) {
            return true
        }
        val fallbackDir = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir
        val target = java.io.File(fallbackDir, name)
        val retry = DownloadManager.Request(Uri.parse(url)).apply {
            setTitle(file.title?.ifBlank { null } ?: name)
            setDescription(name)
            setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            setDestinationUri(Uri.fromFile(target))
        }
        return runCatching { manager.enqueue(retry) }.isSuccess
    }

    private fun guessMime(filename: String): String {
        val lower = filename.lowercase()
        return when {
            lower.endsWith(".mkv") -> "video/x-matroska"
            lower.endsWith(".mp4") || lower.endsWith(".m4v") -> "video/mp4"
            lower.endsWith(".webm") -> "video/webm"
            lower.endsWith(".mp3") -> "audio/mpeg"
            lower.endsWith(".flac") -> "audio/flac"
            lower.endsWith(".srt") -> "application/x-subrip"
            else -> "application/octet-stream"
        }
    }
}
