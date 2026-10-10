package org.jellyfin.firetv.player

import android.content.Context
import android.graphics.Typeface
import android.view.KeyEvent
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.core.view.isVisible
import org.jellyfin.firetv.R
import org.jellyfin.firetv.core.IptvSourceSnapshot
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Focus stays on complete server rows, including while observations refresh. */
class PlaybackServerPanel(
    private val context: Context, private val root: FrameLayout,
    private val picked: (String) -> Unit, private val tracks: () -> Unit, private val retry: () -> Unit,
) {
    private val actions = mutableListOf<View>()
    private val column = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22), dp(22), dp(22), dp(18)) }
    private val status = text(13f)
    private val list = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private val german get() = Locale.getDefault().language == "de"
    private fun tr(de: String, en: String) = if (german) de else en

    init {
        root.addView(column, FrameLayout.LayoutParams(-1, -1))
        column.addView(text(25f, true).apply { text = tr("Server wechseln", "Switch server") })
        column.addView(status, params(-1, -2, 10))
        val scroll = ScrollView(context).apply { isFocusable = false; isVerticalScrollBarEnabled = false; addView(list) }
        column.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dp(16) })
        column.addView(text(11f).apply { text = tr("↑↓ Auswählen · OK Wechseln · Zurück Schließen", "↑↓ Select · OK Switch · Back Close") }, params(-1, -2, 12))
    }

    fun show() {
        root.animate().withEndAction(null).cancel()
        root.isVisible = true; root.alpha = 0f; root.translationX = dp(24).toFloat()
        root.animate().alpha(1f).translationX(0f).setDuration(180).start()
        (actions.firstOrNull { it.isSelected } ?: actions.firstOrNull())?.requestFocus()
    }

    fun hide() { root.animate().withEndAction(null).cancel(); root.isVisible = false }

    fun render(snapshot: IptvSourceSnapshot?, selectedId: String?, loading: Boolean = false, failed: Boolean = false) {
        val focus = root.findFocus()?.tag
        list.removeAllViews(); actions.clear()
        status.text = when {
            failed -> tr("Serverliste konnte nicht geladen werden.", "Could not load server choices.")
            snapshot?.accountReason == "ProviderBusy" -> tr("Anbieter-Verbindungslimit erreicht. Keine Aussage über tote Sender.", "Provider connection limit reached. Channels are not marked dead.")
            snapshot?.accountReason != null -> tr("Anbieter-Anmeldung prüfen. Hintergrundtests pausieren.", "Check provider credentials. Background tests are paused.")
            loading -> tr("Gespeicherte Messwerte werden geladen…", "Loading saved observations…")
            else -> tr("Wechsel nur für diese Wiedergabe. Messungen erfolgen im Leerlauf; kein Ping.", "Switch this playback only. Media checks run when idle; these are not ping times.")
        }
        row("tracks", tr("Ton & Untertitel", "Audio & subtitles"), "", false, tracks)
        if (failed) row("retry", tr("Erneut laden", "Retry"), "", false, retry)
        if (snapshot != null && snapshot.automaticId.isNotBlank()) {
            val automatic = selectedId.isNullOrBlank() || selectedId == snapshot.automaticId || !selectedId.contains("_iptv_")
            row(snapshot.automaticId, tr("Automatisch", "Automatic"), tr("Standardserver und geprüfte Ersatzquellen", "Default server and verified alternatives"), automatic) { picked(snapshot.automaticId) }
            snapshot.sources.forEach { source ->
                val label = when (source.status) {
                    "Reachable" -> tr("Zuletzt erreichbar", "Recently playable")
                    "Unstable" -> tr("Instabil", "Unstable")
                    "Failed" -> tr("Mehrfach fehlgeschlagen", "Repeated failures")
                    else -> tr("Ungeprüft", "Unknown")
                }
                val time = source.checkedAt?.let { runCatching {
                    DateTimeFormatter.ofPattern("dd.MM. HH:mm").withZone(ZoneId.systemDefault()).format(Instant.parse(it))
                }.getOrNull() }
                val details = listOfNotNull(
                    label,
                    source.startMs?.let { tr("Bild", "Media") + " " + String.format(Locale.getDefault(), "%.1f s", it / 1000.0) },
                    time,
                    if (time == null) null else if (source.channelSpecific) tr("Dieser Sender", "This channel") else tr("Server-Stichprobe", "Server sample"),
                ).joinToString(" · ")
                val suffix = when {
                    snapshot.playingId == source.id -> tr(" · Läuft jetzt", " · Playing now")
                    source.isDefault -> tr(" · Standard", " · Default")
                    else -> ""
                }
                row(source.mediaSourceId, source.name + suffix, details, selectedId == source.mediaSourceId) { picked(source.mediaSourceId) }
            }
        } else if (!loading && !failed) {
            status.text = tr("Für diesen Sender sind keine Ersatzserver konfiguriert.", "No alternative servers configured for this channel.")
        }
        if (root.isVisible) (actions.firstOrNull { it.tag == focus } ?: actions.firstOrNull { it.isSelected } ?: actions.firstOrNull())?.requestFocus()
    }

    fun dispatch(event: KeyEvent): Boolean {
        if (event.keyCode !in listOf(KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT)) return false
        if (event.action == KeyEvent.ACTION_DOWN) {
            val index = actions.indexOf(root.findFocus()).coerceAtLeast(0)
            val delta = when (event.keyCode) { KeyEvent.KEYCODE_DPAD_UP -> -1; KeyEvent.KEYCODE_DPAD_DOWN -> 1; else -> 0 }
            actions.getOrNull((index + delta).coerceIn(0, (actions.size - 1).coerceAtLeast(0)))?.requestFocus()
        }
        return true
    }

    private fun row(key: String, title: String, detail: String, selected: Boolean, action: () -> Unit) {
        val row = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL; tag = key; id = View.generateViewId()
            isFocusable = true; isClickable = true; isSelected = selected
            setBackgroundResource(R.drawable.bg_track_row); setPadding(dp(14), dp(12), dp(14), dp(12))
            addView(text(17f, true).apply { text = (if (selected) "✓  " else "") + title })
            if (detail.isNotBlank()) addView(text(12f).apply { text = detail }, params(-1, -2, 4))
            setOnClickListener { action() }
        }
        actions.add(row); list.addView(row, params(-1, -2, 7))
    }

    private fun text(size: Float, bold: Boolean = false) = TextView(context).apply {
        textSize = size; setTextColor(if (bold) 0xFFF0F5FF.toInt() else 0xFFB7C6DC.toInt())
        typeface = Typeface.create(if (bold) "sans-serif-medium" else "sans-serif", Typeface.NORMAL)
        isFocusable = false; setTextIsSelectable(false); includeFontPadding = false
    }
    private fun params(w: Int, h: Int, top: Int) = LinearLayout.LayoutParams(w, h).apply { topMargin = dp(top) }
    private fun dp(value: Int) = (context.resources.displayMetrics.density * value + .5f).toInt()
}
