package org.jellyfin.firetv.download

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.webkit.CookieManager
import org.jellyfin.firetv.core.DownloadRequests
import org.jellyfin.firetv.core.StreamAuth

object FileDownloader {
    data class EnqueueResult(val started: Int, val duplicates: Int)

    private val recentUrls = LinkedHashMap<String, Long>()

    fun enqueue(context: Context, json: String, accessToken: String? = null): EnqueueResult {
        val parsed = DownloadRequests.parseDetailed(json)
        val token = parsed.accessToken?.ifBlank { null } ?: accessToken?.ifBlank { null }
        if (parsed.files.isEmpty()) {
            return EnqueueResult(0, 0)
        }
        val manager = context.getSystemService(Context.DOWNLOAD_SERVICE) as? DownloadManager
            ?: return EnqueueResult(0, 0)
        var started = 0
        var duplicates = 0
        for (file in parsed.files) {
            when (enqueueOne(context, manager, file, token)) {
                EnqueueOutcome.STARTED -> started++
                EnqueueOutcome.DUPLICATE -> duplicates++
                EnqueueOutcome.FAILED -> Unit
            }
        }
        return EnqueueResult(started, duplicates)
    }

    private enum class EnqueueOutcome { STARTED, DUPLICATE, FAILED }

    private fun enqueueOne(
        context: Context,
        manager: DownloadManager,
        file: DownloadRequests.File,
        accessToken: String?,
    ): EnqueueOutcome {
        val url = StreamAuth.withAccessToken(file.url, accessToken.orEmpty())
        if (isDuplicate(url)) {
            return EnqueueOutcome.DUPLICATE
        }
        val name = DownloadRequests.safeFilename(
            file.filename ?: file.title,
            fallback = file.itemId?.let { "jellyfin-$it" } ?: "jellyfin-download",
        )
        val request = buildRequest(url, name, file.title, accessToken)
        val publicOk = runCatching {
            request.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, "Jellyfin/$name")
            val id = manager.enqueue(request)
            DownloadIndex.remember(context, id)
        }.isSuccess
        if (publicOk) {
            return EnqueueOutcome.STARTED
        }
        val fallbackDir = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir
        val target = java.io.File(fallbackDir, name)
        val retry = buildRequest(url, name, file.title, accessToken)
        val fallbackOk = runCatching {
            retry.setDestinationUri(Uri.fromFile(target))
            val id = manager.enqueue(retry)
            DownloadIndex.remember(context, id)
        }.isSuccess
        return if (fallbackOk) EnqueueOutcome.STARTED else EnqueueOutcome.FAILED
    }

    private fun buildRequest(
        url: String,
        name: String,
        title: String?,
        accessToken: String?,
    ): DownloadManager.Request {
        return DownloadManager.Request(Uri.parse(url)).apply {
            setTitle(title?.ifBlank { null } ?: name)
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
    }

    private fun isDuplicate(url: String): Boolean {
        val now = System.currentTimeMillis()
        val iterator = recentUrls.entries.iterator()
        while (iterator.hasNext()) {
            if (now - iterator.next().value > 8_000) {
                iterator.remove()
            }
        }
        if (recentUrls.containsKey(url)) {
            return true
        }
        recentUrls[url] = now
        return false
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
            lower.endsWith(".vtt") -> "text/vtt"
            lower.endsWith(".ass") || lower.endsWith(".ssa") -> "text/x-ssa"
            else -> "application/octet-stream"
        }
    }
}
