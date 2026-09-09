package org.jellyfin.firetv.player

import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.media.AudioManager
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.KeyEvent
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.CaptionStyleCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.jellyfin.firetv.R
import org.jellyfin.firetv.core.JellyfinHttp
import org.jellyfin.firetv.core.LivePlayback
import org.jellyfin.firetv.core.LiveTvNowNextText
import org.jellyfin.firetv.core.MediaTrack
import org.jellyfin.firetv.core.MediaTracks
import org.jellyfin.firetv.core.PlaybackPayload
import org.jellyfin.firetv.core.PlayerSyncState
import org.jellyfin.firetv.core.RemoteSubtitle
import org.jellyfin.firetv.core.RemoteSubtitles
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.StreamAuth
import org.jellyfin.firetv.core.StreamResolver
import org.jellyfin.firetv.core.SubtitleSearchOutcome
import org.jellyfin.firetv.databinding.ActivityPlayerBinding
import java.util.Locale
import java.util.concurrent.TimeUnit

class PlayerActivity : AppCompatActivity(), PlayerCommands.Listener {
    private lateinit var binding: ActivityPlayerBinding
    private var player: ExoPlayer? = null
    private var playback: ResolvedPlayback? = null
    private var reporter: PlaybackReporter? = null
    private var resolveJob: Job? = null
    private var originalPayload: String? = null
    private var queuePayload: String? = null
    private var ignoreSsl: Boolean = false
    private var pausedBySystem: Boolean = false
    private var liveRetries: Int = 0
    private var lastEmittedVolume: Int = 100
    private var lastServerProgressAt: Long = 0L
    private val mainHandler = Handler(Looper.getMainLooper())
    private val hideOsd = Runnable {
        val keepAudio = playback?.isAudio == true
        if (!keepAudio && !binding.trackPanel.isVisible && !binding.searchPanel.isVisible && player?.isPlaying == true) {
            binding.osd.isVisible = false
        }
    }
    private val osdTick = object : Runnable {
        override fun run() {
            if (!binding.osd.isVisible) {
                return
            }
            updateOsd()
            mainHandler.postDelayed(this, 400)
        }
    }
    private val stallWatchdog = Runnable {
        val exo = player ?: return@Runnable
        if (exo.playbackState == Player.STATE_BUFFERING) {
            Toast.makeText(this, R.string.playback_timeout, Toast.LENGTH_LONG).show()
            stopAndClose()
        }
    }
    private val progressTick = object : Runnable {
        override fun run() {
            val exo = player ?: return
            updateOsd()
            val current = playback
            if (current != null) {
                emitSync("timeupdate")
                val now = SystemClock.elapsedRealtime()
                if (now - lastServerProgressAt >= 10_000) {
                    lastServerProgressAt = now
                    lifecycleScope.launch(Dispatchers.IO) {
                        reporter?.progress(exo.currentPosition, !exo.isPlaying)
                    }
                }
            }
            mainHandler.postDelayed(this, 2_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityPlayerBinding.inflate(layoutInflater)
        setContentView(binding.root)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
        window.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        binding.osdSeek.isFocusable = false
        binding.osdSeek.isFocusableInTouchMode = false
        styleSubtitles()
        PlayerCommands.listener = this
        ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, false)
        beginResolve(intent.getStringExtra(EXTRA_PAYLOAD))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, ignoreSsl)
        queuePayload = null
        beginResolve(intent.getStringExtra(EXTRA_PAYLOAD), resetQueue = true, resetRetries = true)
    }

    private fun beginResolve(payload: String?, resetQueue: Boolean = true, resetRetries: Boolean = true) {
        if (payload.isNullOrBlank()) {
            if (playback == null) {
                finish()
            }
            return
        }
        originalPayload = payload
        if (resetQueue) {
            queuePayload = payload
        }
        if (resetRetries) {
            liveRetries = 0
        }
        resolveJob?.cancel()
        val liveHint = LivePlayback.isLivePayload(payload)
        binding.loading.isVisible = true
        binding.loadingTitle.setText(R.string.app_name)
        binding.loadingHint.setText(if (liveHint) R.string.preparing_live else R.string.preparing_playback)
        resolveJob = lifecycleScope.launch {
            val resolved = withContext(Dispatchers.IO) {
                runCatching {
                    withTimeout(if (liveHint) 40_000 else 25_000) {
                        StreamResolver.resolve(payload, ignoreSsl)
                    }
                }
            }
            if (!isActiveSafe()) {
                return@launch
            }
            val playback = resolved.getOrNull()
            if (playback == null) {
                val reason = when (val error = resolved.exceptionOrNull()) {
                    is TimeoutCancellationException -> getString(R.string.playback_timeout)
                    else -> error?.message ?: getString(R.string.playback_failed)
                }
                Toast.makeText(this@PlayerActivity, reason, Toast.LENGTH_LONG).show()
                finish()
                return@launch
            }
            startPlayer(playback)
        }
    }

    private fun isActiveSafe(): Boolean {
        return !isFinishing && !isDestroyed
    }

    private fun styleSubtitles() {
        binding.playerView.subtitleView?.apply {
            setStyle(
                CaptionStyleCompat(
                    Color.WHITE,
                    Color.TRANSPARENT,
                    Color.TRANSPARENT,
                    CaptionStyleCompat.EDGE_TYPE_OUTLINE,
                    Color.BLACK,
                    Typeface.DEFAULT_BOLD,
                ),
            )
            setFractionalTextSize(0.046f)
            setBottomPaddingFraction(0.16f)
            setApplyEmbeddedFontSizes(false)
        }
    }

    private fun startPlayer(resolved: ResolvedPlayback, resumePositionMs: Long? = null) {
        val keepPosition = resumePositionMs ?: resolved.startPositionMs
        releasePlayer(clearPlayback = false)
        playback = resolved
        reporter = PlaybackReporter(resolved, ignoreSsl)
        binding.osdTitle.text = liveChannelTitle() ?: resolved.title
        binding.loadingTitle.text = resolved.title
        binding.loadingHint.setText(R.string.preparing_playback)
        val headers = linkedMapOf<String, String>()
        headers["Accept-Language"] = JellyfinHttp.acceptLanguage()
        if (resolved.accessToken.isNotBlank()) {
            headers["X-Emby-Token"] = resolved.accessToken
            headers["Authorization"] = JellyfinHttp.authorization(
                resolved.appName,
                resolved.deviceName,
                resolved.deviceId,
                resolved.appVersion,
                resolved.accessToken,
            )
        }
        val dataSourceFactory = DefaultHttpDataSource.Factory()
            .setUserAgent("${resolved.appName}/${resolved.appVersion}")
            .setConnectTimeoutMs(if (resolved.isLive) 20_000 else 12_000)
            .setReadTimeoutMs(if (resolved.isLive) 45_000 else 20_000)
            .setAllowCrossProtocolRedirects(true)
            .setDefaultRequestProperties(headers)
        val loadControl = DefaultLoadControl.Builder()
            .setBufferDurationsMs(
                if (resolved.isLive) 2_500 else 3_000,
                if (resolved.isLive) 15_000 else 20_000,
                if (resolved.isLive) 1_500 else 1_000,
                if (resolved.isLive) 3_000 else 2_500,
            )
            .build()
        val exo = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
            .setLoadControl(loadControl)
            .build()
            .also { player = it }
        binding.playerView.player = exo
        binding.playerView.useController = false
        exo.setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(C.USAGE_MEDIA)
                .setContentType(if (resolved.isAudio) C.AUDIO_CONTENT_TYPE_MUSIC else C.AUDIO_CONTENT_TYPE_MOVIE)
                .build(),
            true,
        )
        exo.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_READY) {
                    binding.loading.isVisible = false
                    mainHandler.removeCallbacks(stallWatchdog)
                    applyPreferredTracks(exo, resolved)
                    emitSync("durationchange")
                    emitSync("playing")
                }
                if (playbackState == Player.STATE_BUFFERING) {
                    mainHandler.removeCallbacks(stallWatchdog)
                    mainHandler.postDelayed(stallWatchdog, if (resolved.isLive) 120_000 else 45_000)
                }
                if (playbackState == Player.STATE_ENDED) {
                    emitSync("ended")
                    if (resolved.isLive) {
                        retryLive(getString(R.string.playback_failed))
                    } else if (!playAdjacentFromQueue(next = true)) {
                        stopAndClose()
                    }
                }
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                emitSync(if (isPlaying) "playing" else "pause")
            }

            override fun onPlayerError(error: PlaybackException) {
                emitSync("error")
                if (resolved.isLive) {
                    retryLive(error.message?.takeIf { it.isNotBlank() } ?: getString(R.string.playback_failed))
                    return
                }
                Toast.makeText(
                    this@PlayerActivity,
                    error.message?.takeIf { it.isNotBlank() } ?: getString(R.string.playback_failed),
                    Toast.LENGTH_LONG,
                ).show()
                stopAndClose()
            }
        })
        exo.setMediaItem(mediaItemFor(resolved))
        exo.prepare()
        if (!resolved.isLive && keepPosition > 0) {
            exo.seekTo(keepPosition)
        }
        exo.playWhenReady = true
        lifecycleScope.launch(Dispatchers.IO) { reporter?.playing() }
        emitSync("playing")
        mainHandler.post(progressTick)
        renderTrackPanel()
        showOsd()
        if (resolved.isAudio) {
            mainHandler.removeCallbacks(hideOsd)
            binding.osd.isVisible = true
        }
    }

    private fun mediaItemFor(resolved: ResolvedPlayback): MediaItem {
        val configs = resolved.subtitleTracks.mapNotNull { track ->
            val uri = MediaTracks.sidecarUri(
                resolved.serverAddress,
                resolved.itemId,
                resolved.mediaSourceId,
                track,
            ) ?: return@mapNotNull null
            val flags = if (track.index == resolved.selectedSubtitleIndex) {
                C.SELECTION_FLAG_DEFAULT
            } else {
                0
            }
            MediaItem.SubtitleConfiguration.Builder(Uri.parse(StreamAuth.withAccessToken(uri, resolved.accessToken)))
                .setMimeType(MediaTracks.mimeType(track))
                .setLanguage(track.language ?: "und")
                .setLabel(track.displayTitle)
                .setId(track.index.toString())
                .setSelectionFlags(flags)
                .build()
        }
        val builder = MediaItem.Builder()
            .setUri(resolved.url)
            .setSubtitleConfigurations(configs)
        LivePlayback.mimeType(resolved.container, resolved.url)?.let { builder.setMimeType(it) }
        return builder.build()
    }

    private fun retryLive(message: String) {
        val payload = originalPayload
        if (liveRetries < MAX_LIVE_RETRIES && !payload.isNullOrBlank()) {
            liveRetries += 1
            Toast.makeText(this, R.string.live_reconnecting, Toast.LENGTH_SHORT).show()
            beginResolve(payload, resetQueue = false, resetRetries = false)
            return
        }
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
        stopAndClose()
    }

    private fun playAdjacentFromQueue(next: Boolean): Boolean {
        val source = queuePayload ?: originalPayload ?: return false
        val currentId = playback?.itemId ?: return false
        val targetId = if (next) {
            PlaybackPayload.nextItemId(source, currentId)
        } else {
            PlaybackPayload.previousItemId(source, currentId)
        } ?: return false
        beginResolve(PlaybackPayload.retarget(source, targetId), resetQueue = false, resetRetries = true)
        return true
    }

    private fun applyPreferredTracks(exo: ExoPlayer, resolved: ResolvedPlayback) {
        val builder = exo.trackSelectionParameters.buildUpon()
        resolved.audioTracks.firstOrNull { it.index == resolved.selectedAudioIndex }?.language?.let {
            builder.setPreferredAudioLanguage(it)
        }
        val selectedSub = resolved.selectedSubtitleIndex
        val subtitle = resolved.subtitleTracks.firstOrNull { it.index == selectedSub }
        if (subtitle == null || selectedSub == null || selectedSub < 0) {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
        } else {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
            subtitle.language?.let { builder.setPreferredTextLanguage(it) }
        }
        exo.trackSelectionParameters = builder.build()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action != KeyEvent.ACTION_DOWN) {
            return super.dispatchKeyEvent(event)
        }
        if (binding.searchPanel.isVisible) {
            if (event.keyCode == KeyEvent.KEYCODE_BACK || event.keyCode == KeyEvent.KEYCODE_ESCAPE) {
                hideSearchPanel()
                return true
            }
            return super.dispatchKeyEvent(event)
        }
        if (binding.trackPanel.isVisible) {
            if (event.keyCode == KeyEvent.KEYCODE_BACK || event.keyCode == KeyEvent.KEYCODE_ESCAPE) {
                hideTrackPanel()
                return true
            }
            return super.dispatchKeyEvent(event)
        }
        val exo = player
        if (exo == null) {
            if (event.keyCode == KeyEvent.KEYCODE_BACK || event.keyCode == KeyEvent.KEYCODE_ESCAPE) {
                resolveJob?.cancel()
                finish()
                return true
            }
            return super.dispatchKeyEvent(event)
        }
        return when (event.keyCode) {
            KeyEvent.KEYCODE_DPAD_CENTER,
            KeyEvent.KEYCODE_ENTER,
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE,
            -> {
                if (exo.isPlaying) exo.pause() else exo.play()
                showOsd()
                true
            }
            KeyEvent.KEYCODE_MEDIA_PLAY -> {
                exo.play()
                showOsd()
                true
            }
            KeyEvent.KEYCODE_MEDIA_PAUSE -> {
                exo.pause()
                showOsd()
                true
            }
            KeyEvent.KEYCODE_DPAD_RIGHT,
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD,
            -> {
                if (playback?.isLive == true) {
                    showOsd()
                } else {
                    exo.seekTo(seekTarget(exo, +30_000))
                    showOsd()
                }
                true
            }
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_MEDIA_REWIND,
            -> {
                if (playback?.isLive == true) {
                    showOsd()
                } else {
                    exo.seekTo(seekTarget(exo, -10_000))
                    showOsd()
                }
                true
            }
            KeyEvent.KEYCODE_MEDIA_NEXT,
            -> {
                if (!playAdjacentFromQueue(next = true)) {
                    showOsd()
                }
                true
            }
            KeyEvent.KEYCODE_MEDIA_PREVIOUS,
            -> {
                if (!playAdjacentFromQueue(next = false)) {
                    showOsd()
                }
                true
            }
            KeyEvent.KEYCODE_DPAD_UP,
            -> {
                if (playback?.isLive == true && playAdjacentFromQueue(next = false)) {
                    true
                } else {
                    showTrackPanel()
                    true
                }
            }
            KeyEvent.KEYCODE_DPAD_DOWN,
            -> {
                if (playback?.isLive == true && playAdjacentFromQueue(next = true)) {
                    true
                } else {
                    showOsd()
                    true
                }
            }
            KeyEvent.KEYCODE_CAPTIONS,
            KeyEvent.KEYCODE_MENU,
            -> {
                showTrackPanel()
                true
            }
            KeyEvent.KEYCODE_BACK,
            KeyEvent.KEYCODE_ESCAPE,
            KeyEvent.KEYCODE_MEDIA_STOP,
            -> {
                stopAndClose()
                true
            }
            else -> super.dispatchKeyEvent(event)
        }
    }

    private fun seekTarget(exo: ExoPlayer, deltaMs: Long): Long {
        val duration = exo.duration
        val position = exo.currentPosition.coerceAtLeast(0)
        val next = position + deltaMs
        return if (duration > 0) next.coerceIn(0, duration) else next.coerceAtLeast(0)
    }

    private fun showOsd() {
        binding.osd.isVisible = true
        updateOsd()
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.removeCallbacks(osdTick)
        mainHandler.post(osdTick)
        val keepOpen = binding.trackPanel.isVisible ||
            binding.searchPanel.isVisible ||
            player?.isPlaying == false ||
            playback?.isAudio == true
        if (!keepOpen) {
            mainHandler.postDelayed(hideOsd, 4_500)
        }
    }

    private fun updateOsd() {
        val exo = player ?: return
        val duration = exo.duration
        val position = exo.currentPosition.coerceAtLeast(0)
        binding.osdSeek.max = 1000
        binding.osdSeek.progress = if (duration > 0) ((position * 1000) / duration).toInt() else 0
        binding.osdPlayState.text = if (exo.isPlaying) "▶" else "❚❚"
        if (playback?.isLive == true) {
            binding.osdSeek.progress = 1000
            binding.osdTime.text = getString(R.string.player_live)
            binding.osdRemaining.isVisible = false
            binding.osdHints.setText(R.string.player_hints_live)
        } else if (playback?.isAudio == true) {
            binding.osdTime.text = "${formatTime(position)}  /  ${formatTime(duration)}"
            binding.osdRemaining.isVisible = duration > 0
            if (duration > 0) {
                binding.osdRemaining.text = getString(R.string.player_remaining, formatTime(duration - position))
            }
            binding.osdHints.setText(R.string.player_hints_audio)
        } else {
            binding.osdTime.text = "${formatTime(position)}  /  ${formatTime(duration)}"
            binding.osdRemaining.isVisible = duration > 0
            if (duration > 0) {
                binding.osdRemaining.text = getString(R.string.player_remaining, formatTime(duration - position))
            }
            binding.osdHints.setText(R.string.player_hints)
        }
        binding.osdMeta.text = listOfNotNull(liveGuideLine(), trackSummary())
            .filter { it.isNotBlank() }
            .joinToString("  ·  ")
    }

    private fun liveGuide(): LiveTvNowNextText.Guide? {
        val payload = originalPayload ?: return null
        if (playback?.isLive != true && !LivePlayback.isLivePayload(payload)) {
            return null
        }
        return LiveTvNowNextText.parse(
            PlaybackPayload.itemName(payload),
            PlaybackPayload.itemOriginalTitle(payload),
            PlaybackPayload.itemOverview(payload),
        )
    }

    private fun liveChannelTitle(): String? {
        return liveGuide()?.channelName?.takeIf { it.isNotBlank() }
    }

    private fun liveGuideLine(): String? {
        return liveGuide()?.nowLine()
    }

    private fun trackSummary(): String {
        val current = playback ?: return ""
        val audio = current.audioTracks.firstOrNull { it.index == current.selectedAudioIndex }
            ?: current.audioTracks.firstOrNull { it.isDefault }
            ?: current.audioTracks.firstOrNull()
        val subtitle = current.subtitleTracks.firstOrNull { it.index == current.selectedSubtitleIndex }
        val audioLabel = audio?.displayTitle ?: getString(R.string.track_audio)
        val subLabel = subtitle?.displayTitle ?: getString(R.string.subtitle_off)
        return "$audioLabel · $subLabel"
    }

    private fun formatTime(ms: Long): String {
        if (ms <= 0) {
            return "00:00"
        }
        val hours = TimeUnit.MILLISECONDS.toHours(ms)
        val minutes = TimeUnit.MILLISECONDS.toMinutes(ms) % 60
        val seconds = TimeUnit.MILLISECONDS.toSeconds(ms) % 60
        return if (hours > 0) {
            String.format(Locale.US, "%d:%02d:%02d", hours, minutes, seconds)
        } else {
            String.format(Locale.US, "%02d:%02d", minutes, seconds)
        }
    }

    private fun showTrackPanel() {
        renderTrackPanel()
        binding.trackPanel.isVisible = true
        binding.osd.isVisible = true
        mainHandler.removeCallbacks(hideOsd)
        binding.trackList.getChildAt(1)?.requestFocus() ?: binding.trackList.getChildAt(0)?.requestFocus()
    }

    private fun hideTrackPanel() {
        binding.trackPanel.isVisible = false
        binding.playerView.requestFocus()
        showOsd()
    }

    private fun renderTrackPanel() {
        val current = playback ?: return
        val list = binding.trackList
        list.removeAllViews()
        addHeading(list, getString(R.string.track_audio))
        if (current.audioTracks.isEmpty()) {
            addHeading(list, "—")
        } else {
            current.audioTracks.forEach { track ->
                addTrackButton(list, track.displayTitle, track.index == current.selectedAudioIndex) {
                    onAudioPicked(track)
                }
            }
        }
        addHeading(list, getString(R.string.track_subtitles))
        addTrackButton(
            list,
            getString(R.string.subtitle_off),
            current.selectedSubtitleIndex == null || current.selectedSubtitleIndex!! < 0,
        ) {
            onSubtitlePicked(null)
        }
        current.subtitleTracks.forEach { track ->
            addTrackButton(list, track.displayTitle, track.index == current.selectedSubtitleIndex) {
                onSubtitlePicked(track)
            }
        }
        if (!current.isLive) {
            addTrackButton(list, getString(R.string.subtitle_search), selected = false) {
                showSearchPanel()
            }
        }
    }

    private fun addHeading(parent: LinearLayout, text: String) {
        val view = TextView(this)
        view.text = text.uppercase(Locale.getDefault())
        view.setTextColor(getColor(R.color.accent_soft))
        view.textSize = 13f
        view.letterSpacing = 0.08f
        view.setPadding(8, 20, 8, 6)
        parent.addView(view)
    }

    private fun addTrackButton(
        parent: LinearLayout,
        label: String,
        selected: Boolean,
        onClick: () -> Unit,
    ) {
        val button = layoutInflater.inflate(R.layout.item_track, parent, false) as Button
        button.text = if (selected) "●  $label" else label
        button.setOnClickListener { onClick() }
        parent.addView(button)
    }

    private fun onAudioPicked(track: MediaTrack) {
        val current = playback ?: return
        playback = current.copy(selectedAudioIndex = track.index)
        val exo = player
        if (exo != null && !track.language.isNullOrBlank()) {
            applyPreferredTracks(exo, playback!!)
            renderTrackPanel()
            updateOsd()
            return
        }
        reloadTracks(track.index, current.selectedSubtitleIndex)
    }

    private fun onSubtitlePicked(track: MediaTrack?) {
        val current = playback ?: return
        val index = track?.index ?: -1
        playback = current.copy(selectedSubtitleIndex = index)
        val exo = player
        if (exo != null && (track == null || track.isTextSidecar || hasTextTracks(exo))) {
            applyPreferredTracks(exo, playback!!)
            renderTrackPanel()
            updateOsd()
            return
        }
        reloadTracks(current.selectedAudioIndex, index)
    }

    private fun hasTextTracks(exo: ExoPlayer): Boolean {
        return exo.currentTracks.groups.any { it.type == C.TRACK_TYPE_TEXT && it.length > 0 }
    }

    private fun reloadTracks(audioIndex: Int?, subtitleIndex: Int?) {
        val payload = originalPayload ?: return
        val position = player?.currentPosition ?: playback?.startPositionMs ?: 0L
        binding.loading.isVisible = true
        resolveJob?.cancel()
        resolveJob = lifecycleScope.launch {
            val resolved = withContext(Dispatchers.IO) {
                runCatching {
                    withTimeout(25_000) {
                        StreamResolver.resolve(payload, ignoreSsl, audioIndex, subtitleIndex)
                    }
                }
            }
            if (!isActiveSafe()) {
                return@launch
            }
            val playback = resolved.getOrNull()
            if (playback == null) {
                binding.loading.isVisible = false
                Toast.makeText(this@PlayerActivity, R.string.playback_failed, Toast.LENGTH_LONG).show()
                return@launch
            }
            startPlayer(playback.copy(selectedAudioIndex = audioIndex, selectedSubtitleIndex = subtitleIndex), position)
        }
    }

    private fun showSearchPanel() {
        binding.searchPanel.isVisible = true
        binding.trackPanel.isVisible = false
        binding.searchResults.removeAllViews()
        binding.searchStatus.setText(R.string.subtitle_search_pick_language)
        val row = binding.languageRow
        row.removeAllViews()
        val preferred = RemoteSubtitles.preferredLanguage(Locale.getDefault().toLanguageTag())
        RemoteSubtitles.SEARCH_LANGUAGES.forEach { (code, label) ->
            val button = layoutInflater.inflate(R.layout.item_track, row, false) as Button
            val params = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT,
            )
            params.marginEnd = 12
            button.layoutParams = params
            button.minWidth = 140
            button.text = label
            button.setOnClickListener { searchSubtitles(code) }
            row.addView(button)
            if (code == preferred) {
                button.post { button.requestFocus() }
            }
        }
    }

    private fun hideSearchPanel() {
        binding.searchPanel.isVisible = false
        showTrackPanel()
    }

    private fun searchSubtitles(language: String) {
        val current = playback ?: return
        binding.searchStatus.setText(R.string.subtitle_search_searching)
        binding.searchResults.removeAllViews()
        lifecycleScope.launch {
            val outcome = withContext(Dispatchers.IO) {
                runCatching {
                    RemoteSubtitles.search(
                        serverAddress = current.serverAddress,
                        accessToken = current.accessToken,
                        itemId = current.itemId,
                        language = language,
                        ignoreSslErrors = ignoreSsl,
                        deviceId = current.deviceId,
                        deviceName = current.deviceName,
                        appName = current.appName,
                        appVersion = current.appVersion,
                    )
                }.getOrElse { SubtitleSearchOutcome.Failed(0, it.message.orEmpty()) }
            }
            if (!isActiveSafe()) {
                return@launch
            }
            when (outcome) {
                is SubtitleSearchOutcome.Success -> renderSearchResults(outcome.items)
                is SubtitleSearchOutcome.Failed -> {
                    binding.searchStatus.text = when (outcome.httpCode) {
                        401, 403 -> getString(R.string.subtitle_search_forbidden)
                        else -> getString(R.string.subtitle_search_failed, outcome.httpCode)
                    }
                }
            }
        }
    }

    private fun renderSearchResults(items: List<RemoteSubtitle>) {
        binding.searchResults.removeAllViews()
        if (items.isEmpty()) {
            binding.searchStatus.setText(R.string.subtitle_search_empty)
            return
        }
        binding.searchStatus.text = getString(R.string.subtitle_search_pick_language)
        items.take(40).forEach { item ->
            val button = layoutInflater.inflate(R.layout.item_track, binding.searchResults, false) as Button
            button.text = item.label()
            button.setOnClickListener { applyRemoteSubtitle(item) }
            binding.searchResults.addView(button)
        }
        binding.searchResults.getChildAt(0)?.requestFocus()
    }

    private fun applyRemoteSubtitle(item: RemoteSubtitle) {
        val current = playback ?: return
        val payload = originalPayload ?: return
        val position = player?.currentPosition ?: current.startPositionMs
        binding.searchStatus.setText(R.string.subtitle_applying)
        lifecycleScope.launch {
            val downloaded = withContext(Dispatchers.IO) {
                runCatching {
                    RemoteSubtitles.download(
                        serverAddress = current.serverAddress,
                        accessToken = current.accessToken,
                        itemId = current.itemId,
                        subtitleId = item.id,
                        ignoreSslErrors = ignoreSsl,
                        deviceId = current.deviceId,
                        deviceName = current.deviceName,
                        appName = current.appName,
                        appVersion = current.appVersion,
                    )
                }.getOrDefault(false)
            }
            if (!downloaded) {
                binding.searchStatus.setText(R.string.subtitle_download_failed)
                Toast.makeText(this@PlayerActivity, R.string.subtitle_download_failed, Toast.LENGTH_LONG).show()
                return@launch
            }
            val resolved = withContext(Dispatchers.IO) {
                runCatching {
                    withTimeout(25_000) {
                        StreamResolver.resolve(payload, ignoreSsl, current.selectedAudioIndex, null)
                    }
                }.getOrNull()
            }
            if (!isActiveSafe() || resolved == null) {
                binding.searchStatus.setText(R.string.subtitle_download_failed)
                return@launch
            }
            val added = resolved.subtitleTracks.lastOrNull()
                ?: resolved.subtitleTracks.firstOrNull { it.language.equals(item.language, true) }
            Toast.makeText(this@PlayerActivity, R.string.subtitle_applied, Toast.LENGTH_SHORT).show()
            binding.searchPanel.isVisible = false
            startPlayer(
                resolved.copy(selectedSubtitleIndex = added?.index ?: resolved.selectedSubtitleIndex),
                position,
            )
            showTrackPanel()
        }
    }

    override fun pause() {
        player?.pause()
        showOsd()
    }

    override fun resume() {
        player?.play()
        showOsd()
    }

    override fun stop() {
        stopAndClose()
    }

    override fun seekMs(positionMs: Long) {
        if (playback?.isLive != true) {
            player?.seekTo(positionMs.coerceAtLeast(0))
        }
        showOsd()
    }

    override fun setVolume(percent: Int) {
        val audio = getSystemService(AudioManager::class.java)
            ?: getSystemService(android.content.Context.AUDIO_SERVICE) as AudioManager
        val max = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
        val target = ((percent.coerceIn(0, 100) / 100f) * max).toInt()
        audio.setStreamVolume(AudioManager.STREAM_MUSIC, target, 0)
        lastEmittedVolume = percent.coerceIn(0, 100)
        emitSync("volumechange")
    }

    private fun emitSync(event: String) {
        val exo = player
        val current = playback
        val json = PlayerSyncState.json(
            event = event,
            positionMs = exo?.currentPosition?.coerceAtLeast(0) ?: current?.startPositionMs ?: 0L,
            durationMs = exo?.duration?.takeIf { it > 0 } ?: 0L,
            paused = exo?.isPlaying != true,
            volume = lastEmittedVolume,
            itemId = current?.itemId,
            isLive = current?.isLive == true,
        )
        PlayerWebSync.emit(json)
    }

    override fun destroy() {
        stopAndClose()
    }

    override fun setAudioStreamIndex(index: Int) {
        val track = playback?.audioTracks?.firstOrNull { it.index == index } ?: return
        onAudioPicked(track)
    }

    override fun nextTrack() {
        playAdjacentFromQueue(next = true)
    }

    override fun previousTrack() {
        playAdjacentFromQueue(next = false)
    }

    override fun setSubtitleStreamIndex(index: Int) {
        if (index < 0) {
            onSubtitlePicked(null)
            return
        }
        val track = playback?.subtitleTracks?.firstOrNull { it.index == index }
        if (track == null) {
            reloadTracks(playback?.selectedAudioIndex, index)
            return
        }
        onSubtitlePicked(track)
    }

    private fun stopAndClose() {
        emitSync("playbackstop")
        val exo = player
        val position = exo?.currentPosition ?: 0L
        val closing = playback
        lifecycleScope.launch(Dispatchers.IO) {
            reporter?.stopped(position)
            if (closing != null) {
                StreamResolver.closeLiveStream(closing, ignoreSsl)
            }
        }
        releasePlayer()
        finish()
    }

    private fun releasePlayer(clearPlayback: Boolean = true) {
        mainHandler.removeCallbacks(progressTick)
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.removeCallbacks(osdTick)
        mainHandler.removeCallbacks(stallWatchdog)
        binding.playerView.player = null
        player?.release()
        player = null
        if (clearPlayback) {
            playback = null
        }
    }

    override fun onStart() {
        super.onStart()
        if (pausedBySystem) {
            player?.play()
            pausedBySystem = false
        }
    }

    override fun onStop() {
        super.onStop()
        if (isFinishing) {
            releasePlayer()
        } else if (player?.isPlaying == true) {
            player?.pause()
            pausedBySystem = true
        }
    }

    override fun onDestroy() {
        resolveJob?.cancel()
        if (PlayerCommands.listener === this) {
            PlayerCommands.listener = null
        }
        releasePlayer()
        super.onDestroy()
    }

    companion object {
        const val EXTRA_PAYLOAD = "payload"
        const val EXTRA_IGNORE_SSL = "ignore_ssl"
        private const val MAX_LIVE_RETRIES = 3
    }
}
