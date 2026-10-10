package org.jellyfin.firetv.player

import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.text.TextUtils
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.core.view.isVisible
import org.jellyfin.firetv.R
import org.jellyfin.firetv.core.IptvSourceSnapshot
import org.jellyfin.firetv.core.MediaTrack
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.TrackPresentation
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.abs

/** One accordion drawer. Only headers and actions receive remote focus. */
class PlaybackTrackPanel(
    private val context: Context,
    private val root: FrameLayout,
    private val audioPicked: (MediaTrack) -> Unit,
    private val subtitlePicked: (MediaTrack?) -> Unit,
    private val offsetChanged: (Long) -> Unit,
    private val synchronize: () -> Unit,
    private val syncStatus: () -> Unit,
    private val refresh: () -> Unit,
    private val search: () -> Unit,
    private val serverPicked: (String) -> Unit,
    private val retryServers: () -> Unit,
    private val retryPlayback: () -> Unit,
) {
    private enum class Section { SERVERS, AUDIO, SUBTITLES, TIMING }
    private var section: Section? = null
    private var current: ResolvedPlayback? = null
    private var itemId: String? = null
    private var live = false
    private var canTime = false
    private var offsetMs = 0L
    private var lastFocus: String? = null
    private var snapshot: IptvSourceSnapshot? = null
    private var selectedServer: String? = null
    private var serversLoading = false
    private var serversFailed = false
    private var failure = ""
    private val actions = mutableListOf<View>()
    private val mediaTitle = text(15f, muted = true)
    private val list = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private val scroll = ScrollView(context).apply { isVerticalScrollBarEnabled = false; isFocusable = false }
    private val job = text(13f, muted = true)
    private var timingValue: TextView? = null
    private var timingHint: TextView? = null
    private var earlier: View? = null
    private var later: View? = null
    private var closing = false
    private fun tr(de: String, en: String) = if (Locale.getDefault().language == "de") de else en

    init {
        val column = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22), dp(20), dp(22), dp(16)) }
        root.addView(column, FrameLayout.LayoutParams(-1, -1))
        column.addView(text(11f, muted = true).apply { text = tr("WIEDERGABE", "PLAYBACK"); letterSpacing = .15f })
        column.addView(text(25f, bold = true).apply { text = tr("Bild & Ton", "Playback options") }, vertical(-1, -2, 6))
        mediaTitle.maxLines = 2; mediaTitle.ellipsize = TextUtils.TruncateAt.END
        column.addView(mediaTitle, vertical(-1, -2, 6))
        column.addView(View(context).apply { setBackgroundColor(0x304F6687) }, vertical(-1, 1, 14))
        scroll.addView(list, FrameLayout.LayoutParams(-1, -2))
        column.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dp(10) })
        job.maxLines = 2; job.isVisible = false; job.setTextColor(0xFF9FD3FF.toInt())
        column.addView(job, vertical(-1, -2, 10))
        column.addView(text(11f, muted = true).apply {
            text = tr("↑↓ Auswählen · OK Aufklappen · ← Zuklappen", "↑↓ Select · OK Expand · ← Collapse")
        }, vertical(-1, -2, 12))
    }

    fun render(value: ResolvedPlayback?, id: String?, title: String, isLive: Boolean, offset: Long, timingAvailable: Boolean) {
        if (itemId != id) {
            section = null; lastFocus = null; snapshot = null; failure = ""; setJobStatus("")
        }
        itemId = id; current = value; live = isLive; offsetMs = offset; canTime = timingAvailable
        if (!live && section == Section.SERVERS) section = null
        mediaTitle.text = title
        renderContent()
    }

    fun servers(value: IptvSourceSnapshot?, selected: String?, loading: Boolean = false, failed: Boolean = false) {
        snapshot = value; selectedServer = selected; serversLoading = loading; serversFailed = failed
        renderContent()
    }

    fun unavailable(message: String) { failure = message; section = Section.SERVERS; renderContent("retry-playback") }
    fun clearFailure() { failure = "" }
    fun subtitles() { section = Section.SUBTITLES }
    fun setJobStatus(message: String) { job.text = message; job.isVisible = message.isNotBlank() }

    fun show() {
        val hidden = !root.isVisible
        root.animate().withEndAction(null).cancel(); closing = false; root.isVisible = true
        if (hidden) { root.alpha = 0f; root.translationX = dp(24).toFloat() }
        root.animate().alpha(1f).translationX(0f).setDuration(180).setInterpolator(DecelerateInterpolator()).start()
        (actions.firstOrNull { it.tag == lastFocus } ?: actions.firstOrNull())?.requestFocus()
    }

    fun hide(animate: Boolean = true, onHidden: () -> Unit = {}) {
        if (closing && animate) return
        root.animate().withEndAction(null).cancel(); closing = true
        val finish = { root.isVisible = false; root.alpha = 1f; root.translationX = 0f; closing = false; onHidden() }
        if (!animate) finish() else root.animate().alpha(0f).translationX(dp(24).toFloat()).setDuration(140).withEndAction(finish).start()
    }

    fun updateOffset(value: Long) {
        offsetMs = value
        timingValue?.text = context.getString(R.string.track_panel_offset, value / 1000.0)
        timingHint?.text = when {
            value > 0 -> context.getString(R.string.track_panel_later, abs(value) / 1000.0)
            value < 0 -> context.getString(R.string.track_panel_earlier, abs(value) / 1000.0)
            else -> str(R.string.track_panel_original_timing)
        }
    }

    fun dispatch(event: KeyEvent): Boolean {
        if (closing) return true
        val code = event.keyCode
        if (code !in listOf(KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT)) return false
        if (event.action != KeyEvent.ACTION_DOWN) return true
        val focused = root.findFocus()
        val index = actions.indexOf(focused).coerceAtLeast(0)
        val header = (focused?.tag as? String)?.takeIf { it.startsWith("header:") }
        when (code) {
            KeyEvent.KEYCODE_DPAD_LEFT -> {
                if (focused === later) earlier?.requestFocus()
                else if (section != null) { val key = "header:$section"; section = null; renderContent(key) }
            }
            KeyEvent.KEYCODE_DPAD_RIGHT -> {
                if (focused === earlier) later?.requestFocus()
                else if (header != null) {
                    section = Section.valueOf(header.substringAfter(':')); renderContent(header)
                    actions.getOrNull(actions.indexOf(root.findFocus()) + 1)?.requestFocus()
                }
            }
            KeyEvent.KEYCODE_DPAD_UP -> actions.getOrNull((index - if (focused === later) 2 else 1).coerceAtLeast(0))?.requestFocus()
            KeyEvent.KEYCODE_DPAD_DOWN -> actions.getOrNull((index + if (focused === earlier) 2 else 1).coerceAtMost(actions.lastIndex))?.requestFocus()
        }
        return true
    }

    private fun renderContent(requestedFocus: String? = null) {
        val focusKey = requestedFocus ?: root.findFocus()?.tag as? String ?: lastFocus
        actions.clear(); list.removeAllViews(); timingValue = null; timingHint = null; earlier = null; later = null
        if (failure.isNotBlank()) {
            note(failure)
            row("retry-playback", tr("Erneut verbinden", "Reconnect"), tr("Oder unten einen anderen Server wählen", "Or choose another server below"), action = retryPlayback)
        }
        val value = current
        Section.entries.filter { it != Section.SERVERS || live }.forEach { area ->
            val label = when (area) {
                Section.SERVERS -> tr("Server", "Server")
                Section.AUDIO -> str(R.string.track_audio)
                Section.SUBTITLES -> str(R.string.track_subtitles)
                Section.TIMING -> str(R.string.track_panel_timing)
            }
            val summary = when (area) {
                Section.SERVERS -> snapshot?.sources?.firstOrNull { it.mediaSourceId == selectedServer }?.name ?: tr("Automatisch", "Automatic")
                Section.AUDIO -> value?.audioTracks?.firstOrNull { it.index == value.selectedAudioIndex }?.let { label(it, 0) } ?: tr("Standard", "Default")
                Section.SUBTITLES -> value?.subtitleTracks?.firstOrNull { it.index == value.selectedSubtitleIndex }?.let { label(it, 0) } ?: str(R.string.subtitle_off)
                Section.TIMING -> context.getString(R.string.track_panel_offset, offsetMs / 1000.0)
            }
            row("header:$area", (if (section == area) "▾  " else "▸  ") + label, summary) {
                section = if (section == area) null else area
                renderContent("header:$area")
            }
            if (section != area) return@forEach
            if (area == Section.SERVERS) { renderServers(); return@forEach }
            if (value == null) { note(tr("Nach dem Verbindungsaufbau verfügbar", "Available after connecting")); return@forEach }
            when (area) {
                Section.SERVERS -> Unit
                Section.AUDIO -> {
                    val selected = value.selectedAudioIndex ?: value.audioTracks.firstOrNull { it.isDefault }?.index ?: value.audioTracks.firstOrNull()?.index
                    value.audioTracks.forEachIndexed { i, track ->
                        row("audio:${track.identity ?: track.index}", label(track, i), distinctMetadata(track, i, value.audioTracks), selected == track.index) { audioPicked(track) }
                    }
                    if (value.audioTracks.isEmpty()) note(str(R.string.track_panel_audio_empty))
                }
                Section.SUBTITLES -> {
                    row("off", str(R.string.subtitle_off), str(R.string.track_panel_subtitles_off), (value.selectedSubtitleIndex ?: -1) < 0) { subtitlePicked(null) }
                    value.subtitleTracks.forEachIndexed { i, track ->
                        row("subtitle:${track.identity ?: track.index}", label(track, i), distinctMetadata(track, i, value.subtitleTracks), value.selectedSubtitleIndex == track.index) { subtitlePicked(track) }
                    }
                    if (!value.isLive) {
                        row("search", str(R.string.subtitle_search), str(R.string.track_panel_search_hint)) { search() }
                        row("refresh", str(R.string.track_panel_refresh), str(R.string.track_panel_refresh_hint)) { refresh() }
                    }
                }
                Section.TIMING -> {
                    if (!canTime) {
                        note(str(R.string.track_panel_pick_text))
                        row("choose-subtitle", str(R.string.track_panel_choose_subtitle), "") { section = Section.SUBTITLES; renderContent("header:SUBTITLES") }
                    } else {
                        val selected = value.subtitleTracks.firstOrNull { it.index == value.selectedSubtitleIndex }
                        note(selected?.let { label(it, value.subtitleTracks.indexOf(it)) }.orEmpty())
                        val stepper = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
                        list.addView(stepper, vertical(-1, 62, 8))
                        earlier = stepButton("earlier", "−", str(R.string.subtitle_earlier)) { offsetChanged(-500) }
                        later = stepButton("later", "+", str(R.string.subtitle_later)) { offsetChanged(500) }
                        stepper.addView(earlier, LinearLayout.LayoutParams(dp(54), -1))
                        timingValue = text(26f, bold = true).apply { gravity = Gravity.CENTER }
                        stepper.addView(timingValue, LinearLayout.LayoutParams(0, -1, 1f))
                        stepper.addView(later, LinearLayout.LayoutParams(dp(54), -1))
                        timingHint = note("")
                        updateOffset(offsetMs)
                        row("reset", str(R.string.track_panel_reset), "") { offsetChanged(-offsetMs) }
                        row("sync", str(R.string.track_panel_auto), str(R.string.track_panel_auto_hint)) { synchronize() }
                        row("status", str(R.string.track_panel_job), str(R.string.track_panel_job_hint)) { syncStatus() }
                        row("refresh", str(R.string.track_panel_refresh), str(R.string.track_panel_refresh_hint)) { refresh() }
                    }
                }
            }
        }
        if (root.isVisible) (actions.firstOrNull { it.tag == focusKey } ?: actions.firstOrNull())?.requestFocus()
    }

    private fun renderServers() {
        note(when {
            serversFailed -> tr("Serverliste nicht erreichbar. Erneut laden.", "Server list unavailable. Try again.")
            snapshot?.accountReason == "ProviderBusy" -> tr("Anbieter-Verbindungslimit erreicht.", "Provider connection limit reached.")
            snapshot?.accountReason != null -> tr("Anbieter-Anmeldung prüfen.", "Check provider credentials.")
            serversLoading -> tr("Gespeicherte Messwerte werden geladen…", "Loading saved observations…")
            else -> tr("Nur diese Wiedergabe · Messungen im Leerlauf", "This playback only · Measured when idle")
        })
        if (serversFailed) row("reload-servers", tr("Serverliste erneut laden", "Reload server list"), "", action = retryServers)
        val data = snapshot ?: return
        if (data.automaticId.isBlank()) { note(tr("Keine Ersatzserver konfiguriert", "No alternative servers configured")); return }
        row(data.automaticId, tr("Automatisch", "Automatic"), tr("Standard und geprüfte Ersatzquellen", "Default and verified alternatives"), selectedServer.isNullOrBlank() || selectedServer == data.automaticId || selectedServer?.contains("_iptv_") == false) { serverPicked(data.automaticId) }
        data.sources.forEach { source ->
            val status = when (source.status) {
                "Reachable" -> tr("Zuletzt erreichbar", "Recently playable")
                "Unstable" -> tr("Instabil", "Unstable")
                "Failed" -> tr("Mehrfach fehlgeschlagen", "Repeated failures")
                else -> tr("Ungeprüft", "Unknown")
            }
            val at = source.checkedAt?.let { runCatching { DateTimeFormatter.ofPattern("dd.MM. HH:mm").withZone(ZoneId.systemDefault()).format(Instant.parse(it)) }.getOrNull() }
            val detail = listOfNotNull(status, source.startMs?.let { tr("Bild", "Media") + " " + String.format(Locale.getDefault(), "%.1f s", it / 1000.0) }, at,
                if (at == null) null else if (source.channelSpecific) tr("Dieser Sender", "This channel") else tr("Server-Stichprobe", "Server sample")).joinToString(" · ")
            val suffix = when {
                data.playingId == source.id -> tr(" · Läuft jetzt", " · Playing now")
                source.isDefault -> tr(" · Standard", " · Default")
                else -> ""
            }
            row(source.mediaSourceId, source.name + suffix, detail, selectedServer == source.mediaSourceId) { serverPicked(source.mediaSourceId) }
        }
    }

    private fun label(track: MediaTrack, index: Int): String = TrackPresentation.language(track, Locale.getDefault())
        ?: context.getString(R.string.track_panel_unknown_language, index + 1)

    private fun metadata(track: MediaTrack): String {
        val parts = mutableListOf<String>()
        if (track.type == MediaTrack.Kind.AUDIO) {
            TrackPresentation.audioFormat(track).takeIf { it.isNotBlank() }?.let(parts::add)
            if (TrackPresentation.isCommentary(track)) parts.add(str(R.string.track_panel_commentary))
            if (track.isDefault) parts.add(str(R.string.track_panel_default))
        } else {
            if (TrackPresentation.isSynchronized(track)) parts.add(str(R.string.track_panel_synced))
            if (TrackPresentation.isAiGenerated(track)) parts.add(str(R.string.track_panel_ai))
            if (track.isForced) parts.add(str(R.string.track_panel_forced))
            if (TrackPresentation.isHearingImpaired(track)) parts.add("SDH")
            if (parts.isEmpty()) parts.add(str(if (track.isExternal) R.string.track_panel_external else R.string.track_panel_embedded))
            track.codec?.uppercase(Locale.ROOT)?.takeIf { it.isNotBlank() }?.let { parts.add(if (it == "SUBRIP") "SRT" else it.take(12)) }
        }
        return parts.joinToString(" · ")
    }

    private fun distinctMetadata(track: MediaTrack, index: Int, tracks: List<MediaTrack>): String {
        val detail = metadata(track)
        val duplicates = tracks.count { it.language == track.language && metadata(it) == detail }
        return if (duplicates > 1) listOf(detail, context.getString(R.string.track_panel_variant, index + 1)).filter { it.isNotBlank() }.joinToString(" · ") else detail
    }

    private fun row(key: String, label: String, detail: String, selected: Boolean = false, action: () -> Unit) {
        val row = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            minimumHeight = dp(if (detail.isBlank()) 48 else 64); setPadding(dp(13), dp(10), dp(12), dp(10))
            setBackgroundResource(R.drawable.bg_track_row); isSelected = selected
        }
        val lines = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        lines.addView(text(17f, bold = true).apply { text = label; maxLines = 2; ellipsize = TextUtils.TruncateAt.END })
        if (detail.isNotBlank()) lines.addView(text(12f, muted = true).apply {
            text = detail; maxLines = 2; ellipsize = TextUtils.TruncateAt.END
        }, vertical(-1, -2, 3))
        row.addView(lines, LinearLayout.LayoutParams(0, -2, 1f))
        if (selected) row.addView(text(21f, bold = true).apply { text = "✓"; setTextColor(0xFF9FD3FF.toInt()); gravity = Gravity.CENTER }, LinearLayout.LayoutParams(dp(28), -2))
        configureAction(row, key, listOf(label, detail, if (selected) str(R.string.track_panel_active) else "").filter { it.isNotBlank() }.joinToString(", "), action)
        list.addView(row, vertical(-1, -2, if (list.childCount == 0) 0 else 7))
    }

    private fun stepButton(key: String, label: String, description: String, action: () -> Unit): View = text(28f).apply {
        text = label; gravity = Gravity.CENTER; setBackgroundResource(R.drawable.bg_track_row)
        configureAction(this, key, description, action)
    }

    private fun configureAction(view: View, key: String, description: String, action: () -> Unit) {
        view.id = View.generateViewId(); view.tag = key; view.isFocusable = true; view.isClickable = true
        view.contentDescription = description
        view.setOnClickListener { action() }
        view.setOnFocusChangeListener { _, focused -> if (focused) lastFocus = key }
        actions.add(view)
    }

    private fun note(label: String): TextView = text(13f, muted = true).apply {
        text = label; setPadding(dp(2), dp(3), dp(2), dp(5)); list.addView(this, vertical(-1, -2, 4))
    }

    private fun text(size: Float, bold: Boolean = false, muted: Boolean = false): TextView = TextView(context).apply {
        textSize = size; setTextColor(if (muted) 0xFFB7C6DC.toInt() else Color.rgb(240, 245, 255))
        typeface = Typeface.create(if (bold) "sans-serif-medium" else "sans-serif", Typeface.NORMAL)
        includeFontPadding = false; isFocusable = false; setTextIsSelectable(false)
    }

    private fun vertical(width: Int, height: Int, margin: Int = 0) = LinearLayout.LayoutParams(if (width < 0) width else dp(width), if (height < 0) height else dp(height)).apply { topMargin = dp(margin) }
    private fun str(id: Int) = context.getString(id)
    private fun dp(value: Int) = (value * context.resources.displayMetrics.density + .5f).toInt()
}
