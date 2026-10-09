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
import org.jellyfin.firetv.core.MediaTrack
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.TrackPresentation
import java.util.Locale
import kotlin.math.abs

/** Small native drawer: all focus targets are actions, never labels or film text. */
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
) {
    private enum class Section { AUDIO, SUBTITLES, TIMING }
    private var section = Section.AUDIO
    private var current: ResolvedPlayback? = null
    private var canTime = false
    private var offsetMs = 0L
    private val nav = mutableListOf<View>()
    private val actions = mutableListOf<View>()
    private val remembered = mutableMapOf<Section, String>()
    private val title = text(24f, bold = true)
    private val mediaTitle = text(14f, muted = true)
    private val contentTitle = text(18f, bold = true)
    private val list = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
    private val scroll = ScrollView(context).apply { isFillViewport = false; isVerticalScrollBarEnabled = false; isFocusable = false }
    private val job = text(13f, muted = true)
    private var timingValue: TextView? = null
    private var timingHint: TextView? = null
    private var earlier: View? = null
    private var later: View? = null
    private var closing = false
    private var rendering = false

    init {
        val column = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(22), dp(20), dp(22), dp(16))
        }
        root.addView(column, FrameLayout.LayoutParams(-1, -1))
        column.addView(text(11f, muted = true).apply {
            text = str(R.string.track_panel_eyebrow); letterSpacing = .15f
        })
        title.setText(R.string.tracks_title)
        column.addView(title, vertical(-1, -2, 6))
        mediaTitle.maxLines = 1; mediaTitle.ellipsize = TextUtils.TruncateAt.END
        column.addView(mediaTitle, vertical(-1, -2, 4))
        column.addView(View(context).apply { setBackgroundColor(0x304F6687) }, vertical(-1, 1, 16))
        val body = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL }
        column.addView(body, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dp(16) })
        val rail = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        body.addView(rail, LinearLayout.LayoutParams(dp(94), -1).apply { marginEnd = dp(14) })
        val labels = listOf(R.string.track_audio, R.string.track_subtitles, R.string.track_panel_timing)
        val symbols = listOf("♫", "CC", "↔")
        Section.entries.forEachIndexed { index, value ->
            val tab = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER
                isFocusable = true; isClickable = true; id = View.generateViewId()
                setBackgroundResource(R.drawable.bg_track_row)
                addView(text(21f, bold = true).apply { text = symbols[index]; gravity = Gravity.CENTER })
                addView(text(12f, bold = true).apply { text = str(labels[index]); gravity = Gravity.CENTER })
                contentDescription = str(labels[index])
                setOnFocusChangeListener { _, focused -> if (focused && !rendering && section != value) { section = value; renderContent() } }
                setOnClickListener { section = value; renderContent(); focusContent() }
            }
            rail.addView(tab, vertical(-1, 70, if (index == 0) 0 else 8)); nav.add(tab)
        }
        val content = LinearLayout(context).apply { orientation = LinearLayout.VERTICAL }
        body.addView(content, LinearLayout.LayoutParams(0, -1, 1f))
        content.addView(contentTitle, vertical(-1, -2))
        scroll.addView(list, FrameLayout.LayoutParams(-1, -2))
        content.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dp(10) })
        job.maxLines = 1; job.ellipsize = TextUtils.TruncateAt.END; job.isVisible = false
        job.setTextColor(0xFF9FD3FF.toInt())
        column.addView(job, vertical(-1, -2, 12))
        column.addView(text(11f, muted = true).apply { text = str(R.string.track_panel_navigation) }, vertical(-1, -2, 12))
    }

    fun render(value: ResolvedPlayback, offset: Long, timingAvailable: Boolean) {
        if (current?.itemId != value.itemId) {
            section = Section.AUDIO; remembered.clear(); setJobStatus("")
        }
        current = value; offsetMs = offset; canTime = timingAvailable
        mediaTitle.text = value.title
        renderContent()
    }

    fun show() {
        root.animate().withEndAction(null).cancel()
        val wasHidden = !root.isVisible
        // Android assigns initial focus when a hidden subtree becomes visible. That
        // must not activate the first sidebar tab and discard the section being reopened.
        rendering = true
        closing = false; root.isVisible = true
        if (wasHidden) { root.alpha = 0f; root.translationX = dp(30).toFloat() }
        root.animate().alpha(1f).translationX(0f).setDuration(180).setInterpolator(DecelerateInterpolator()).start()
        // Reopening chooses the actual active track, independent of a previously hovered row.
        focusContent(preferActive = true)
        rendering = false
    }

    fun hide(animate: Boolean = true, onHidden: () -> Unit = {}) {
        if (closing && animate) return
        root.animate().withEndAction(null).cancel()
        closing = true
        val finish = { root.isVisible = false; root.alpha = 1f; root.translationX = 0f; closing = false; onHidden() }
        if (!animate) finish() else root.animate().alpha(0f).translationX(dp(24).toFloat())
            .setDuration(140).withEndAction(finish).start()
    }

    fun subtitles() { section = Section.SUBTITLES }
    fun setJobStatus(message: String) { job.text = message; job.isVisible = message.isNotBlank() }

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
        if (code !in setOf(KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT)) return false
        val focused = root.findFocus()
        val tab = nav.indexOf(focused)
        if (tab >= 0) {
            when (code) {
                KeyEvent.KEYCODE_DPAD_UP -> nav[(tab - 1).coerceAtLeast(0)].requestFocus()
                KeyEvent.KEYCODE_DPAD_DOWN -> nav[(tab + 1).coerceAtMost(nav.lastIndex)].requestFocus()
                KeyEvent.KEYCODE_DPAD_RIGHT -> focusContent()
            }
        } else {
            val index = actions.indexOf(focused)
            when (code) {
                KeyEvent.KEYCODE_DPAD_LEFT -> if (focused === later) earlier?.requestFocus() else nav[section.ordinal].requestFocus()
                KeyEvent.KEYCODE_DPAD_RIGHT -> if (focused === earlier) later?.requestFocus()
                KeyEvent.KEYCODE_DPAD_UP -> {
                    val previous = if (focused === later) index - 2 else index - 1
                    actions.getOrNull(previous.coerceAtLeast(0))?.requestFocus()
                }
                KeyEvent.KEYCODE_DPAD_DOWN -> {
                    val next = if (focused === earlier) index + 2 else index + 1
                    actions.getOrNull(next.coerceAtMost(actions.lastIndex))?.requestFocus()
                }
            }
        }
        return true
    }

    private fun renderContent() {
        val value = current ?: return
        rendering = true
        val focusKey = root.findFocus()?.tag as? String
        val hadContentFocus = actions.contains(root.findFocus())
        actions.clear(); list.removeAllViews(); timingValue = null; timingHint = null; earlier = null; later = null
        nav.forEachIndexed { index, view -> view.isSelected = index == section.ordinal }
        contentTitle.setText(when (section) {
            Section.AUDIO -> R.string.track_panel_audio_title
            Section.SUBTITLES -> R.string.track_subtitles
            Section.TIMING -> R.string.track_panel_timing_title
        })
        when (section) {
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
                    row("choose-subtitle", str(R.string.track_panel_choose_subtitle), "") { section = Section.SUBTITLES; renderContent(); focusContent(true) }
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
        if (hadContentFocus) (actions.firstOrNull { it.tag == focusKey } ?: actions.firstOrNull { it.isSelected } ?: actions.firstOrNull())?.requestFocus()
        else scroll.scrollTo(0, 0)
        rendering = false
    }

    private fun focusContent(preferActive: Boolean = false) {
        val rememberedView = if (preferActive) null else actions.firstOrNull { it.tag == remembered[section] }
        (rememberedView ?: actions.firstOrNull { it.isSelected } ?: actions.firstOrNull() ?: nav[section.ordinal]).requestFocus()
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
        view.setOnFocusChangeListener { _, focused -> if (focused) remembered[section] = key }
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
