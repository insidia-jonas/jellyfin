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
import android.text.Html
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.Gravity
import android.widget.TextView
import android.widget.Button
import android.widget.LinearLayout
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.activity.OnBackPressedCallback
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
import androidx.media3.common.Tracks
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.text.CueGroup
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.HttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.source.LoadEventInfo
import androidx.media3.exoplayer.source.MediaLoadData
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy
import androidx.media3.extractor.DefaultExtractorsFactory
import androidx.media3.extractor.ts.DefaultTsPayloadReaderFactory
import androidx.media3.ui.CaptionStyleCompat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import org.jellyfin.firetv.R
import org.jellyfin.firetv.BuildConfig
import org.jellyfin.firetv.core.HttpCancellation
import org.jellyfin.firetv.core.IptvSources
import org.jellyfin.firetv.core.IptvSourceSnapshot
import org.jellyfin.firetv.core.LiveTuneState
import org.jellyfin.firetv.core.LiveTvChannels
import org.jellyfin.firetv.core.LiveTvChannel
import org.jellyfin.firetv.core.JellyfinHttp
import org.jellyfin.firetv.core.LivePlayback
import org.jellyfin.firetv.core.LiveTvNowNextText
import org.jellyfin.firetv.core.MediaTrack
import org.jellyfin.firetv.core.MediaTracks
import org.jellyfin.firetv.core.PlaybackPayload
import org.jellyfin.firetv.core.PlaybackBuffers
import org.jellyfin.firetv.core.PlaybackMetadata
import org.jellyfin.firetv.core.PlayerPayloadStore
import org.jellyfin.firetv.core.PlayerSyncState
import org.jellyfin.firetv.core.RemoteSubtitle
import org.jellyfin.firetv.core.RemoteSubtitles
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.StreamAuth
import org.jellyfin.firetv.core.StreamResolver
import org.jellyfin.firetv.core.SubtitleSearchOutcome
import org.jellyfin.firetv.core.SubtitleTiming
import org.jellyfin.firetv.core.SubtitleSync
import org.jellyfin.firetv.databinding.ActivityPlayerBinding
import java.util.Locale
import java.io.IOException
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.TimeUnit

@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
class PlayerActivity : AppCompatActivity(), PlayerCommands.Listener {
    private lateinit var binding: ActivityPlayerBinding
    private lateinit var trackDrawer: PlaybackTrackPanel
    private val osdActions = mutableListOf<TextView>()
    private var liveFailed = false
    private var desiredPlayWhenReady = true
    private var serverChoices: IptvSourceSnapshot? = null
    private var serverChoicesItem: String? = null
    private var serverChoicesJob: Job? = null
    private var serverChoicesHttp: HttpCancellation? = null
    private var subtitleSyncJob: Job? = null
    private var subtitleStatusItem: String? = null
    private var player: ExoPlayer? = null
    private var playback: ResolvedPlayback? = null
    private var reporter: PlaybackReporter? = null
    private var resolveHttp: HttpCancellation? = null
    private var pendingTune: Runnable? = null
    private var resolveDeadline: Runnable? = null
    private var cleanupFuture: Future<*>? = null
    private val tune = LiveTuneState()
    private var playerListener: Player.Listener? = null
    private var playerIsLive = false
    private var subtitleOffsetMs = 0L
    private var mediaSourceFactory: DefaultMediaSourceFactory? = null
    private var pendingSubtitleOffset: Runnable? = null
    private var subtitleSearchJob: Job? = null
    private var subtitleSearchGeneration = 0
    private var tuneStartedAt = 0L
    private var bufferingStartedAt = 0L
    private var hasStartedPlayback = false
    private var displayMetadata = PlaybackMetadata()
    private var metadataJob: Job? = null
    private var metadataHttp: HttpCancellation? = null
    private val metadataCache = linkedMapOf<String, PlaybackMetadata>()
    private var stableSince = 0L
    private var liveGuideChannel: LiveTvChannel? = null
    private var guideJob: Job? = null
    private var nextGuideAt = 0L
    private var originalPayload: String? = null
    private var queuePayload: String? = null
    private var ignoreSsl: Boolean = false
    private var pausedBySystem: Boolean = false
    private var lastEmittedVolume: Int = 100
    private var lastServerProgressAt: Long = 0L
    private val mainHandler = Handler(Looper.getMainLooper())
    private val showRebuffering = Runnable {
        if (hasStartedPlayback && player?.playbackState == Player.STATE_BUFFERING) {
            binding.rebuffering.isVisible = true
        }
    }
    private val hideOsd = Runnable {
        val keepAudio = playback?.isAudio == true
        if (!keepAudio && !binding.trackPanel.isVisible && !binding.searchPanel.isVisible && player?.isPlaying == true) {
            dismissOsd()
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
            if (playback?.isLive == true) {
                retryLive()
            } else {
                Toast.makeText(this, R.string.playback_timeout, Toast.LENGTH_LONG).show()
                stopAndClose()
            }
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
                    val position = exo.currentPosition
                    val paused = !exo.isPlaying
                    val currentReporter = reporter
                    currentReporter?.progress(position, paused)
                }
            }
            if (current?.isLive == true) {
                if (exo.isPlaying && stableSince > 0 && SystemClock.elapsedRealtime() - stableSince >= 30_000) {
                    tune.stablePlayback()
                }
                refreshLiveGuide(current)
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
        trackDrawer = PlaybackTrackPanel(this, binding.trackPanel,
            audioPicked = ::onAudioPicked, subtitlePicked = ::onSubtitlePicked,
            offsetChanged = ::adjustSubtitleOffset,
            synchronize = {
                androidx.appcompat.app.AlertDialog.Builder(this)
                    .setTitle(R.string.subtitle_sync_start)
                    .setMessage(R.string.subtitle_sync_confirm)
                    .setPositiveButton(R.string.track_panel_sync_confirm) { _, _ -> subtitleSyncAction(true) }
                    .setNegativeButton(android.R.string.cancel, null).show()
            },
            syncStatus = { subtitleSyncAction(false, explicit = true) },
            refresh = ::refreshSubtitleTracks, search = ::showSearchPanel,
            serverPicked = ::switchLiveSource, retryServers = ::loadServerChoices,
            retryPlayback = { beginResolve(originalPayload, resetQueue = false, playWhenReady = desiredPlayWhenReady) })
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = handlePlayerBack()
        })
        PlayerCommands.listener = this
        ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, false)
        beginResolve(payloadFrom(intent))
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, ignoreSsl)
        queuePayload = null
        beginResolve(payloadFrom(intent), resetQueue = true, resetRetries = true)
    }

    private fun payloadFrom(intent: Intent): String? {
        return PlayerPayloadStore.take(intent.getStringExtra(EXTRA_PAYLOAD_ID))
            ?: intent.getStringExtra(EXTRA_PAYLOAD)
    }

    private fun cancelPendingTune() {
        resolveHttp?.cancel()
        resolveHttp = null
        pendingTune?.let { mainHandler.removeCallbacks(it) }
        pendingTune = null
        resolveDeadline?.let { mainHandler.removeCallbacks(it) }
        resolveDeadline = null
    }

    private fun beginResolve(
        payload: String?,
        resetQueue: Boolean = true,
        resetRetries: Boolean = true,
        delayMs: Long = 0,
        audioStreamIndex: Int? = null,
        subtitleStreamIndex: Int? = null,
        resumePositionMs: Long? = null,
        playWhenReady: Boolean = true,
    ) {
        if (payload.isNullOrBlank()) {
            if (playback == null) finish()
            return
        }
        val confirmedLive = playback?.takeIf { it.itemId == PlaybackPayload.itemId(payload) }?.isLive == true
        cancelPendingTune()
        if (resetRetries || PlaybackPayload.itemId(payload) != originalPayload?.let(PlaybackPayload::itemId)) cancelServerChoices()
        val effectivePayload = if (confirmedLive) org.json.JSONObject(payload).put("IsLiveStream", true).toString() else payload
        originalPayload = effectivePayload
        desiredPlayWhenReady = playWhenReady
        liveFailed = false
        if (::trackDrawer.isInitialized) trackDrawer.clearFailure()
        if (resetQueue) queuePayload = payload
        val generation = tune.select(PlaybackPayload.itemId(payload), resetRetries)
        val liveHint = LivePlayback.isLivePayload(effectivePayload)
        retirePlayback(keepPlayer = liveHint && playerIsLive)
        tuneStartedAt = SystemClock.elapsedRealtime()
        liveGuideChannel = null
        nextGuideAt = 0
        binding.loading.isVisible = true
        binding.loadingTitle.text = if (liveHint) PlaybackPayload.itemName(payload) else PlaybackMetadata.fromPayload(payload).title
        showLoadingHint(liveHint, buffering = false)
        Log.i("FireTvPlayback", "tune request=$generation live=$liveHint retry=${tune.retries}")
        val pending = Runnable {
            pendingTune = null
            val cancellation = HttpCancellation()
            resolveHttp = cancellation
            val cleanup = cleanupFuture
            val ssl = ignoreSsl
            val deadline = Runnable {
                if (tune.accepts(generation) && resolveHttp === cancellation) {
                    cancellation.cancel()
                    tune.invalidate()
                    resolveHttp = null
                    handleResolveFailure(liveHint)
                }
            }
            resolveDeadline = deadline
            mainHandler.postDelayed(deadline, if (liveHint) tune.attemptTimeoutMs(40_000) else 30_000)
            resolverExecutor.execute {
                val result = runCatching {
                    // Release the old tuner slot before opening another stream.
                    // The stop report closes the tuner too. Wait for that HTTP request,
                    // not a second, racing LiveStreams/Close request. Keep tune cancellation responsive.
                    while (cleanup != null) {
                        cancellation.checkActive()
                        try {
                            cleanup.get(100, TimeUnit.MILLISECONDS)
                            break
                        } catch (_: java.util.concurrent.TimeoutException) {
                            // The Activity's overall deadline cancels this tune.
                        }
                    }
                    cancellation.checkActive()
                    StreamResolver.resolve(effectivePayload, ssl, audioStreamIndex, subtitleStreamIndex, cancellation)
                }
                mainHandler.post {
                    mainHandler.removeCallbacks(deadline)
                    val resolved = result.getOrNull()
                    if (!tune.accepts(generation) || cancellation.isCancelled || !isActiveSafe()) {
                        if (resolved != null) cleanupExecutor.execute { StreamResolver.closeLiveStream(resolved, ssl) }
                        return@post
                    }
                    resolveHttp = null
                    resolveDeadline = null
                    if (resolved == null) {
                        handleResolveFailure(liveHint)
                    } else {
                        Log.i("FireTvPlayback", "resolved request=$generation ms=${SystemClock.elapsedRealtime() - tuneStartedAt}")
                        startPlayer(resolved, resumePositionMs, playWhenReady)
                    }
                }
            }
        }
        pendingTune = pending
        mainHandler.postDelayed(pending, delayMs)
    }

    private fun handleResolveFailure(live: Boolean) {
        if (live) retryLive()
        else {
            Toast.makeText(this, R.string.playback_timeout, Toast.LENGTH_LONG).show()
            stopAndClose()
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
            // Leave a safe TV overscan margin without lifting dialogue into the picture.
            setBottomPaddingFraction(0.055f)
            setApplyEmbeddedFontSizes(false)
        }
    }

    private fun startPlayer(resolved: ResolvedPlayback, resumePositionMs: Long? = null, playWhenReady: Boolean = true) {
        if (subtitleStatusItem != resolved.itemId) {
            subtitleSyncJob?.cancel()
            subtitleStatusItem = null
            binding.osdSubtitleJob.isVisible = false
        }
        val keepPosition = resumePositionMs ?: resolved.startPositionMs
        if (playback != null) retirePlayback(keepPlayer = resolved.isLive && playerIsLive)
        playback = resolved
        if (resolved.isLive) {
            // Generic Video channel tiles may only acquire their live identity from
            // PlaybackInfo. Keep that evidence for retries after retiring the player.
            originalPayload = originalPayload?.let { org.json.JSONObject(it).put("IsLiveStream", true).toString() }
        }
        pendingSubtitleOffset?.let(mainHandler::removeCallbacks)
        pendingSubtitleOffset = null
        subtitleOffsetMs = subtitleTimingKey(resolved)?.let { getSharedPreferences("subtitle-timing", MODE_PRIVATE).getLong(it, 0) } ?: 0
        subtitleOffsetMs = SubtitleTiming.clamp(subtitleOffsetMs)
        reporter = PlaybackReporter(resolved, ignoreSsl)
        hasStartedPlayback = false
        displayMetadata = resolved.metadata
        renderPlaybackMetadata(resolved)
        showLoadingHint(resolved.isLive, buffering = false)
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
            .setConnectTimeoutMs(8_000)
            .setReadTimeoutMs(if (resolved.isLive) 30_000 else 12_000)
            .setAllowCrossProtocolRedirects(true)
            .setDefaultRequestProperties(headers)
        val reused = player != null && playerIsLive == resolved.isLive
        if (player != null && !reused) releasePlayer(clearPlayback = false)
        val buffers = PlaybackBuffers.forPlayback(resolved.isLive)
        val loadControl = DefaultLoadControl.Builder()
            .setBufferDurationsMs(
                buffers.minMs, buffers.maxMs, buffers.startMs, buffers.rebufferMs,
            )
            .setTargetBufferBytes(buffers.targetBytes)
            .setPrioritizeTimeOverSizeThresholds(false)
            .build()
        val extractors = DefaultExtractorsFactory()
        if (resolved.isLive) {
            // Some IPTV H.264 streams (including Das Erste) use non-IDR I slices.
            // Waiting exclusively for IDRs leaves ExoPlayer buffering forever.
            extractors.setTsExtractorFlags(DefaultTsPayloadReaderFactory.FLAG_ALLOW_NON_IDR_KEYFRAMES)
        }
        val mediaSourceFactory = DefaultMediaSourceFactory(dataSourceFactory, extractors)
            .setSubtitleParserFactory(OffsetSubtitleParserFactory(subtitleOffsetMs))
        this.mediaSourceFactory = mediaSourceFactory
        if (!resolved.isLive) {
            // Media3 resumes the same HTTP range after transient failures. Keep the
            // playback session and position instead of resolving the whole film again.
            mediaSourceFactory.setLoadErrorHandlingPolicy(DefaultLoadErrorHandlingPolicy(5))
        }
        val exo = player ?: ExoPlayer.Builder(this)
            .setMediaSourceFactory(mediaSourceFactory)
            .setLoadControl(loadControl)
            .build()
            .also { player = it }
        playerIsLive = resolved.isLive
        playerListener?.let { exo.removeListener(it) }
        binding.playerView.player = exo
        Log.i("FireTvPlayback", "player reused=$reused request=${tune.generation}")
        binding.playerView.useController = false
        exo.setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(C.USAGE_MEDIA)
                .setContentType(if (resolved.isAudio) C.AUDIO_CONTENT_TYPE_MUSIC else C.AUDIO_CONTENT_TYPE_MOVIE)
                .build(),
            true,
        )
        val requestGeneration = tune.generation
        val listener = object : Player.Listener {
            private var reportedSubtitleCue = false
            private var reportedSubtitleOffset = Long.MIN_VALUE

            override fun onRenderedFirstFrame() {
                if (!tune.accepts(requestGeneration)) return
                Log.i("FireTvPlayback", "first_frame request=$requestGeneration ms=${SystemClock.elapsedRealtime() - tuneStartedAt}")
            }
            override fun onTracksChanged(tracks: Tracks) {
                if (!tune.accepts(requestGeneration)) return
                playback?.let { applyPreferredTracks(exo, it) }
                if (BuildConfig.DEBUG) {
                    val selected = tracks.groups.filter { it.type == C.TRACK_TYPE_TEXT }.any { group ->
                        (0 until group.length).any { group.isTrackSelected(it) && MediaTracks.matchesSubtitleId(group.getTrackFormat(it).id, playback?.selectedSubtitleIndex) }
                    }
                    Log.i("FireTvPlayback", "subtitle exact_selected=$selected")
                }
            }

            override fun onCues(cueGroup: CueGroup) {
                if (BuildConfig.DEBUG && (!reportedSubtitleCue || reportedSubtitleOffset != subtitleOffsetMs) && cueGroup.cues.isNotEmpty()) {
                    reportedSubtitleCue = true
                    reportedSubtitleOffset = subtitleOffsetMs
                    Log.i("FireTvPlayback", "subtitle first_cue count=${cueGroup.cues.size} position_ms=${exo.currentPosition} cue_time_us=${cueGroup.presentationTimeUs} offset_ms=$subtitleOffsetMs")
                }
            }
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (!tune.accepts(requestGeneration) || playback == null) return
                if (playbackState == Player.STATE_READY) {
                    hasStartedPlayback = true
                    binding.loading.isVisible = false
                    mainHandler.removeCallbacks(showRebuffering)
                    binding.rebuffering.isVisible = false
                    mainHandler.removeCallbacks(stallWatchdog)
                    stableSince = SystemClock.elapsedRealtime()
                    if (bufferingStartedAt > 0) {
                        Log.i("FireTvPlayback", "buffer_end request=$requestGeneration ms=${stableSince - bufferingStartedAt}")
                        bufferingStartedAt = 0
                    }
                    playback?.let { applyPreferredTracks(exo, it) }
                    emitSync("durationchange")
                    emitSync("playing")
                }
                if (playbackState == Player.STATE_BUFFERING) {
                    mainHandler.removeCallbacks(stallWatchdog)
                    stableSince = 0
                    bufferingStartedAt = SystemClock.elapsedRealtime()
                    binding.loading.isVisible = !hasStartedPlayback
                    mainHandler.removeCallbacks(showRebuffering)
                    binding.rebuffering.isVisible = false
                    binding.rebufferingHint.text = if (resolved.isLive && tune.retries > 0) {
                        getString(R.string.live_retry_status, tune.retries, 3)
                    } else getString(R.string.playback_buffering)
                    if (hasStartedPlayback) mainHandler.postDelayed(showRebuffering, 750)
                    showLoadingHint(resolved.isLive, buffering = true)
                    Log.i("FireTvPlayback", "buffer_start request=$requestGeneration live=${resolved.isLive} method=${resolved.playMethod} position_ms=${exo.currentPosition} buffered_ms=${exo.totalBufferedDuration}")
                    mainHandler.postDelayed(stallWatchdog, if (resolved.isLive) tune.attemptTimeoutMs(30_000) else 45_000)
                }
                if (playbackState == Player.STATE_ENDED) {
                    if (resolved.isLive) {
                        retryLive()
                    } else {
                        emitSync("ended")
                        if (!playAdjacentFromQueue(next = true)) stopAndClose()
                    }
                }
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (!tune.accepts(requestGeneration) || playback == null) return
                emitSync(if (isPlaying) "playing" else "pause")
                scheduleOsdHide()
                reporter?.progress(exo.currentPosition, !isPlaying)
            }

            override fun onPlayerError(error: PlaybackException) {
                if (!tune.accepts(requestGeneration) || playback == null) return
                Log.i("FireTvPlayback", "error request=$requestGeneration code=${error.errorCode}")
                if (resolved.isLive) {
                    retryLive()
                    return
                }
                emitSync("error")
                Toast.makeText(
                    this@PlayerActivity,
                    error.message?.takeIf { it.isNotBlank() } ?: getString(R.string.playback_failed),
                    Toast.LENGTH_LONG,
                ).show()
                stopAndClose()
            }
        }
        playerListener = listener
        exo.addListener(listener)
        if (!reused) {
            exo.addAnalyticsListener(object : AnalyticsListener {
                private var lastBandwidthAt = 0L
                override fun onDroppedVideoFrames(eventTime: AnalyticsListener.EventTime, droppedFrames: Int, elapsedMs: Long) {
                    Log.i("FireTvPlayback", "dropped_frames count=$droppedFrames elapsed_ms=$elapsedMs")
                }
                override fun onBandwidthEstimate(eventTime: AnalyticsListener.EventTime, totalLoadTimeMs: Int, totalBytesLoaded: Long, bitrateEstimate: Long) {
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastBandwidthAt < 30_000) return
                    lastBandwidthAt = now
                    Log.i("FireTvPlayback", "network sample_ms=$totalLoadTimeMs bytes=$totalBytesLoaded bitrate=$bitrateEstimate buffered_ms=${exo.totalBufferedDuration}")
                }
                override fun onLoadError(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData, error: IOException, wasCanceled: Boolean) {
                    val status = (error as? HttpDataSource.InvalidResponseCodeException)?.responseCode ?: 0
                    Log.i("FireTvPlayback", "load_error type=${error.javaClass.simpleName} http=$status canceled=$wasCanceled bytes=${loadEventInfo.bytesLoaded}")
                }
            })
        }
        exo.setMediaSource(mediaSourceFactory.createMediaSource(mediaItemFor(resolved)))
        exo.prepare()
        if (!resolved.isLive && keepPosition > 0) {
            exo.seekTo(keepPosition)
        }
        exo.playWhenReady = playWhenReady
        val currentReporter = reporter
        currentReporter?.playing(keepPosition)
        // A paused track reload must report its real state immediately, even if the
        // previous playback session reported less than ten seconds ago.
        lastServerProgressAt = 0L
        mainHandler.removeCallbacks(progressTick)
        mainHandler.post(progressTick)
        buildOsdActions()
        renderTrackPanel()
        if (binding.trackPanel.isVisible && resolved.isLive) loadServerChoices()
        showOsd()
        refreshPlaybackMetadata(resolved)
        if (resolved.isAudio) {
            mainHandler.removeCallbacks(hideOsd)
            binding.osd.isVisible = true
        }
    }

    private fun mediaItemFor(resolved: ResolvedPlayback): MediaItem {
        val configs = listOfNotNull(MediaTracks.selectedSidecar(resolved.subtitleTracks, resolved.selectedSubtitleIndex)).mapNotNull { track ->
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
                .setId("jellyfin-subtitle-${track.index}")
                .setSelectionFlags(flags)
                .build()
        }
        val builder = MediaItem.Builder()
            .setUri(resolved.url)
            .setSubtitleConfigurations(configs)
        if (BuildConfig.DEBUG) Log.i("FireTvPlayback", "subtitle requested=${resolved.selectedSubtitleIndex} sidecars=${configs.size}")
        LivePlayback.mimeType(resolved.container, resolved.url)?.let { builder.setMimeType(it) }
        if (resolved.isLive) {
            builder.setLiveConfiguration(MediaItem.LiveConfiguration.Builder()
                .setTargetOffsetMs(3_000L + tune.retries * 2_000L)
                .setMinOffsetMs(2_000)
                .setMaxOffsetMs(15_000)
                .build())
        }
        return builder.build()
    }

    private fun renderPlaybackMetadata(current: ResolvedPlayback) {
        val title = if (current.isLive) liveChannelTitle() ?: current.title else displayMetadata.title
        binding.osdTitle.text = title
        binding.loadingTitle.text = title
        binding.osdEpisode.text = displayMetadata.episodeLine.orEmpty()
        binding.osdEpisode.isVisible = !current.isLive && displayMetadata.episodeLine != null
        val overview = displayMetadata.overview?.let { Html.fromHtml(it, Html.FROM_HTML_MODE_LEGACY).toString().trim() }.orEmpty()
        binding.osdOverview.text = overview
        binding.osdOverview.isVisible = !current.isLive && overview.isNotBlank()
    }

    private fun refreshPlaybackMetadata(current: ResolvedPlayback) {
        if (current.isLive) return
        val key = "${current.serverAddress}\n${current.userId}\n${current.itemId}"
        metadataCache[key]?.let {
            displayMetadata = current.metadata.enrichedBy(it)
            renderPlaybackMetadata(current)
            return
        }
        if (!current.metadata.needsLookup) return
        val generation = tune.generation
        val cancellation = HttpCancellation()
        metadataHttp = cancellation
        metadataJob = lifecycleScope.launch {
            val metadata = withContext(Dispatchers.IO) {
                runCatching { PlaybackMetadata.fetch(current, ignoreSsl, cancellation) }.getOrNull()
            }
            if (!tune.accepts(generation) || playback == null || cancellation.isCancelled) return@launch
            metadataHttp = null
            if (metadata != null) {
                displayMetadata = current.metadata.enrichedBy(metadata)
                metadataCache[key] = displayMetadata
                while (metadataCache.size > 24) metadataCache.remove(metadataCache.keys.first())
                renderPlaybackMetadata(current)
            }
        }
    }

    private fun retryLive() {
        val payload = originalPayload
        val delay = tune.retryDelayMs()
        retirePlayback(keepPlayer = delay != null, failed = true)
        if (delay != null && !payload.isNullOrBlank()) {
            Log.i("FireTvPlayback", "retry attempt=${tune.retries} delay_ms=$delay")
            beginResolve(payload, resetQueue = false, resetRetries = false, delayMs = delay, playWhenReady = desiredPlayWhenReady)
            binding.loadingHint.text = getString(R.string.live_retry_status, tune.retries, 3)
            return
        }
        cancelPendingTune()
        liveFailed = true
        binding.loading.isVisible = false
        binding.rebuffering.isVisible = false
        showTrackPanel()
        trackDrawer.unavailable(getString(R.string.live_switch_failed))
    }

    private fun showLoadingHint(live: Boolean, buffering: Boolean) {
        binding.loadingHint.text = when {
            live && tune.retries > 0 -> getString(R.string.live_retry_status, tune.retries, 3)
            live && buffering -> getString(R.string.live_buffering)
            live -> getString(R.string.preparing_live)
            else -> getString(R.string.preparing_playback)
        }
    }

    private fun playAdjacentFromQueue(next: Boolean): Boolean {
        val source = queuePayload ?: originalPayload ?: return false
        val currentId = tune.selectedId ?: playback?.itemId ?: return false
        val targetId = if (next) {
            PlaybackPayload.nextItemId(source, currentId)
        } else {
            PlaybackPayload.previousItemId(source, currentId)
        } ?: return false
        val payload = PlaybackPayload.retarget(source, targetId)
        beginResolve(payload, resetQueue = false, delayMs = if (LivePlayback.isLivePayload(payload)) 180 else 0)
        return true
    }

    private fun applyPreferredTracks(exo: ExoPlayer, resolved: ResolvedPlayback) {
        val builder = exo.trackSelectionParameters.buildUpon()
        resolved.audioTracks.firstOrNull { it.index == resolved.selectedAudioIndex }?.language?.let {
            builder.setPreferredAudioLanguage(it)
        }
        val selectedSub = resolved.selectedSubtitleIndex
        val subtitle = resolved.subtitleTracks.firstOrNull { it.index == selectedSub }
        builder.clearOverridesOfType(C.TRACK_TYPE_TEXT)
        if (subtitle == null || selectedSub == null || selectedSub < 0) {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
        } else {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
            subtitle.language?.let { builder.setPreferredTextLanguage(it) }
            // Language alone can choose a forced track instead of the requested full subtitles.
            for (group in exo.currentTracks.groups) {
                if (group.type != C.TRACK_TYPE_TEXT) continue
                val index = (0 until group.length).firstOrNull {
                    MediaTracks.matchesSubtitleId(group.getTrackFormat(it).id, selectedSub)
                } ?: continue
                builder.setOverrideForType(TrackSelectionOverride(group.mediaTrackGroup, index))
                break
            }
        }
        exo.trackSelectionParameters = builder.build()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val code = event.keyCode
        val down = event.action == KeyEvent.ACTION_DOWN
        if (code == KeyEvent.KEYCODE_BACK || code == KeyEvent.KEYCODE_ESCAPE) {
            if (down && event.repeatCount == 0) handlePlayerBack()
            return true
        }
        if (binding.searchPanel.isVisible) return super.dispatchKeyEvent(event)
        if (code == KeyEvent.KEYCODE_MENU || code == KeyEvent.KEYCODE_CAPTIONS) {
            if (down && event.repeatCount == 0) {
                if (binding.trackPanel.isVisible) hideTrackPanel() else {
                    if (code == KeyEvent.KEYCODE_CAPTIONS) trackDrawer.subtitles()
                    showTrackPanel()
                }
            }
            return true
        }
        if (binding.trackPanel.isVisible && trackDrawer.dispatch(event)) return true
        if (binding.trackPanel.isVisible && code in listOf(KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER)) {
            if (down && event.repeatCount == 0) binding.trackPanel.findFocus()?.performClick()
            return true
        }
        val navigation = code in listOf(KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN,
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER)
        if (navigation) {
            if (!down) return true
            if (!binding.osd.isVisible) { showOsd(); return true }
            val index = osdActions.indexOf(binding.osd.findFocus()).coerceAtLeast(0)
            when (code) {
                KeyEvent.KEYCODE_DPAD_LEFT -> osdActions.getOrNull((index - 1).coerceAtLeast(0))?.requestFocus()
                KeyEvent.KEYCODE_DPAD_RIGHT -> osdActions.getOrNull((index + 1).coerceAtMost(osdActions.lastIndex))?.requestFocus()
                KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> osdActions.getOrNull(index)?.requestFocus()
                else -> if (event.repeatCount == 0) osdActions.getOrNull(index)?.performClick()
            }
            scheduleOsdHide()
            return true
        }
        val media = code in listOf(KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY,
            KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, KeyEvent.KEYCODE_MEDIA_REWIND,
            KeyEvent.KEYCODE_MEDIA_NEXT, KeyEvent.KEYCODE_MEDIA_PREVIOUS, KeyEvent.KEYCODE_MEDIA_STOP,
            KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_CHANNEL_DOWN)
        if (!media) return super.dispatchKeyEvent(event)
        if (!down) return true
        val exo = player
        when (code) {
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> if (event.repeatCount == 0) togglePause()
            KeyEvent.KEYCODE_MEDIA_PLAY -> { desiredPlayWhenReady = true; exo?.play(); showOsd() }
            KeyEvent.KEYCODE_MEDIA_PAUSE -> { desiredPlayWhenReady = false; exo?.pause(); showOsd() }
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, KeyEvent.KEYCODE_MEDIA_REWIND -> {
                if (exo != null && playback?.isLive == false) exo.seekTo(seekTarget(exo, if (code == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD) 30_000 else -10_000))
                showOsd()
            }
            KeyEvent.KEYCODE_MEDIA_NEXT, KeyEvent.KEYCODE_MEDIA_PREVIOUS,
            KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_CHANNEL_DOWN -> {
                val channelKey = code == KeyEvent.KEYCODE_CHANNEL_UP || code == KeyEvent.KEYCODE_CHANNEL_DOWN
                if ((!channelKey || isLiveSelection()) && event.repeatCount == 0) {
                    playAdjacentFromQueue(code == KeyEvent.KEYCODE_MEDIA_NEXT || code == KeyEvent.KEYCODE_CHANNEL_UP)
                }
            }
            KeyEvent.KEYCODE_MEDIA_STOP -> stopAndClose()
        }
        return true
    }

    private fun isLiveSelection(): Boolean = playback?.isLive ?: (originalPayload?.let(LivePlayback::isLivePayload) == true)

    private fun togglePause() {
        val exo = player ?: return
        desiredPlayWhenReady = !exo.playWhenReady
        exo.playWhenReady = desiredPlayWhenReady
        showOsd()
    }

    private fun buildOsdActions() {
        val focus = binding.osd.findFocus()?.tag
        binding.osdActions.removeAllViews(); osdActions.clear()
        fun action(key: String, title: String, clicked: () -> Unit) {
            val view = TextView(this).apply {
                tag = key; id = View.generateViewId(); text = title; contentDescription = title
                textSize = 16f; setTextColor(Color.WHITE); gravity = Gravity.CENTER
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                isFocusable = true; isClickable = true; setTextIsSelectable(false)
                setBackgroundResource(R.drawable.bg_track_row)
                setPadding(20, 8, 20, 8)
                setOnClickListener { clicked(); scheduleOsdHide() }
            }
            binding.osdActions.addView(view, LinearLayout.LayoutParams(0, (48 * resources.displayMetrics.density).toInt(), 1f).apply {
                marginEnd = (8 * resources.displayMetrics.density).toInt()
            })
            osdActions.add(view)
        }
        action("pause", getString(R.string.player_pause), ::togglePause)
        if (!isLiveSelection()) {
            action("rewind", getString(R.string.player_seek_back)) { player?.let { it.seekTo(seekTarget(it, -10_000)) }; updateOsd() }
            action("forward", getString(R.string.player_seek_forward)) { player?.let { it.seekTo(seekTarget(it, 30_000)) }; updateOsd() }
        }
        val queue = queuePayload ?: originalPayload
        val id = playback?.itemId ?: tune.selectedId
        if (queue != null && id != null) {
            if (isLiveSelection() && PlaybackPayload.previousItemId(queue, id) != null) {
                action("previous", getString(R.string.player_previous_channel)) { playAdjacentFromQueue(false) }
            }
            if (PlaybackPayload.nextItemId(queue, id) != null) {
                action("next", getString(if (isLiveSelection()) R.string.player_next_channel else R.string.player_next_item)) { playAdjacentFromQueue(true) }
            }
        }
        action("options", getString(R.string.player_options), ::showTrackPanel)
        if (binding.osd.isVisible) (osdActions.firstOrNull { it.tag == focus } ?: osdActions.firstOrNull())?.requestFocus()
    }

    private fun seekTarget(exo: ExoPlayer, deltaMs: Long): Long {
        val duration = exo.duration
        val position = exo.currentPosition.coerceAtLeast(0)
        val next = position + deltaMs
        return if (duration > 0) next.coerceIn(0, duration) else next.coerceAtLeast(0)
    }

    private fun showOsd() {
        if (binding.trackPanel.isVisible || binding.searchPanel.isVisible) return
        val wasHidden = !binding.osd.isVisible
        binding.osd.isVisible = true
        updateOsd()
        if (wasHidden) {
            binding.osd.alpha = 0f
            binding.osd.animate().alpha(1f).setDuration(140).start()
            osdActions.firstOrNull()?.requestFocus()
        }
        mainHandler.removeCallbacks(osdTick)
        mainHandler.post(osdTick)
        scheduleOsdHide()
    }

    private fun dismissOsd() {
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.removeCallbacks(osdTick)
        binding.osd.animate().cancel()
        binding.osd.isVisible = false
        binding.playerView.requestFocus()
    }

    private fun handlePlayerBack() {
        when {
            binding.searchPanel.isVisible -> hideSearchPanel()
            binding.trackPanel.isVisible -> hideTrackPanel()
            binding.osd.isVisible -> dismissOsd()
            else -> stopAndClose()
        }
    }

    private fun scheduleOsdHide() {
        mainHandler.removeCallbacks(hideOsd)
        val keepOpen = binding.trackPanel.isVisible ||
            binding.searchPanel.isVisible ||
            player?.isPlaying == false ||
            playback?.isAudio == true
        if (binding.osd.isVisible && !keepOpen) {
            mainHandler.postDelayed(hideOsd, 4_500)
        }
    }

    private fun updateOsd() {
        if (!binding.osd.isVisible) return
        val exo = player ?: return
        val duration = exo.duration
        val position = exo.currentPosition.coerceAtLeast(0)
        binding.osdSeek.max = 1000
        binding.osdSeek.progress = if (duration > 0) ((position * 1000) / duration).toInt() else 0
        binding.osdPlayState.updateText(if (exo.playWhenReady) "▶" else "Ⅱ")
        val pauseLabel = getString(if (exo.playWhenReady) R.string.player_pause else R.string.player_continue)
        osdActions.firstOrNull { it.tag == "pause" }?.let { it.updateText(pauseLabel); if (it.contentDescription != pauseLabel) it.contentDescription = pauseLabel }
        binding.osdContext.updateText(getString(when {
            !exo.playWhenReady -> R.string.player_paused
            playback?.isLive == true -> R.string.player_live
            displayMetadata.isEpisode -> R.string.player_episode
            else -> R.string.player_playing
        }))
        binding.osdSeek.isVisible = playback?.isLive != true
        if (playback?.isLive == true) {
            binding.osdSeek.progress = 1000
            binding.osdTime.updateText(getString(R.string.player_live))
            binding.osdRemaining.isVisible = false
            binding.osdHints.updateText(getString(R.string.player_hints_live))
        } else if (playback?.isAudio == true) {
            binding.osdTime.updateText("${formatTime(position)}  /  ${formatTime(duration)}")
            binding.osdRemaining.isVisible = duration > 0
            if (duration > 0) {
                binding.osdRemaining.updateText(getString(R.string.player_remaining, formatTime(duration - position)))
            }
            binding.osdHints.updateText(getString(R.string.player_hints_audio))
        } else {
            binding.osdTime.updateText("${formatTime(position)}  /  ${formatTime(duration)}")
            binding.osdRemaining.isVisible = duration > 0
            if (duration > 0) {
                binding.osdRemaining.updateText(getString(R.string.player_remaining, formatTime(duration - position)))
            }
            binding.osdHints.updateText(getString(R.string.player_hints))
        }
        binding.osdMeta.updateText(listOfNotNull(liveGuideLine(), trackSummary())
            .filter { it.isNotBlank() }
            .joinToString("  ·  "))
    }

    private fun TextView.updateText(value: CharSequence) { if (text.toString() != value.toString()) text = value }

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
        return liveGuideChannel?.let { channel ->
            listOfNotNull(LiveTvChannels.nowLine(channel), LiveTvChannels.nextLine(channel)).joinToString("  ·  ")
        } ?: liveGuide()?.nowLine()
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

    private fun loadServerChoices() {
        val payload = originalPayload ?: return
        if (!isLiveSelection()) return
        val item = PlaybackPayload.itemId(payload) ?: return
        if (serverChoicesItem != item) { serverChoices = null; serverChoicesItem = item }
        cancelServerChoices()
        val cancellation = HttpCancellation()
        serverChoicesHttp = cancellation
        fun selected() = org.json.JSONObject(originalPayload ?: "{}").optString("mediaSourceId").ifBlank { playback?.mediaSourceId }
        trackDrawer.servers(serverChoices, selected(), loading = true)
        serverChoicesJob = lifecycleScope.launch {
            do {
                val liveStream = playback?.liveStreamId
                val result = runCatching { withContext(Dispatchers.IO) { IptvSources.load(payload, ignoreSsl, cancellation, liveStream) } }
                if (!binding.trackPanel.isVisible || originalPayload != payload || serverChoicesHttp !== cancellation) return@launch
                result.getOrNull()?.let { serverChoices = it }
                trackDrawer.servers(serverChoices, selected(), failed = result.isFailure)
                delay(30_000)
            } while (binding.trackPanel.isVisible)
        }
    }

    private fun cancelServerChoices() {
        serverChoicesHttp?.cancel(); serverChoicesHttp = null
        serverChoicesJob?.cancel(); serverChoicesJob = null
    }

    private fun switchLiveSource(sourceId: String) {
        val payload = originalPayload ?: return
        if (!isLiveSelection() || serverChoicesItem != PlaybackPayload.itemId(payload)) return
        val choices = serverChoices ?: return
        if (sourceId.isBlank() || (sourceId != choices.automaticId && choices.sources.none { it.mediaSourceId == sourceId })) return
        val currentSource = org.json.JSONObject(payload).optString("mediaSourceId").ifBlank { playback?.mediaSourceId ?: choices.automaticId }
        if (!liveFailed && playback != null && sourceId == currentSource) { hideTrackPanel(); return }
        val updated = org.json.JSONObject(payload).apply {
            put("mediaSourceId", sourceId); remove("MediaSourceId")
            remove("liveStreamId"); remove("LiveStreamId"); put("startPositionTicks", 0)
        }.toString()
        cancelServerChoices()
        trackDrawer.hide(animate = false)
        // Switching a paused live stream starts the new live edge; there is no shared timeshift buffer.
        beginResolve(updated, resetQueue = false, resetRetries = true, playWhenReady = true)
    }

    private fun showTrackPanel() {
        renderTrackPanel()
        dismissOsd()
        trackDrawer.show()
        mainHandler.removeCallbacks(hideOsd)
        if (isLiveSelection()) loadServerChoices()
        val current = playback
        if (current != null && !current.isLive && subtitleSyncJob?.isActive != true) subtitleSyncAction(false)
    }

    private fun hideTrackPanel() {
        cancelServerChoices()
        trackDrawer.hide {
            binding.playerView.requestFocus()
            if (!liveFailed) showOsd()
        }
    }

    private fun renderTrackPanel() {
        val current = playback
        val payload = originalPayload
        trackDrawer.render(current, current?.itemId ?: payload?.let(PlaybackPayload::itemId),
            current?.title ?: payload?.let(PlaybackPayload::itemName).orEmpty(),
            isLiveSelection(), subtitleOffsetMs, current?.let { subtitleTimingKey(it) } != null)
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
        subtitleOffsetMs = subtitleTimingKey(playback!!)?.let { getSharedPreferences("subtitle-timing", MODE_PRIVATE).getLong(it, 0) } ?: 0
        mediaSourceFactory?.setSubtitleParserFactory(OffsetSubtitleParserFactory(SubtitleTiming.clamp(subtitleOffsetMs)))
        val exo = player
        if (track != null && index != current.selectedSubtitleIndex && MediaTracks.selectedSidecar(current.subtitleTracks, index) != null) {
            reloadTracks(current.selectedAudioIndex, index)
            return
        }
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
        beginResolve(payload, resetQueue = false, audioStreamIndex = audioIndex,
            subtitleStreamIndex = subtitleIndex, resumePositionMs = position, playWhenReady = player?.playWhenReady ?: true)
    }

    private fun showSearchPanel() {
        binding.searchPanel.isVisible = true
        trackDrawer.hide(animate = false)
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
        subtitleSearchGeneration++
        subtitleSearchJob?.cancel()
        binding.searchPanel.isVisible = false
        trackDrawer.subtitles()
        showTrackPanel()
    }

    private fun searchSubtitles(language: String) {
        val current = playback ?: return
        val generation = ++subtitleSearchGeneration
        subtitleSearchJob?.cancel()
        binding.searchStatus.setText(R.string.subtitle_search_searching)
        binding.searchResults.removeAllViews()
        subtitleSearchJob = lifecycleScope.launch {
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
            if (!isActiveSafe() || generation != subtitleSearchGeneration || playback?.itemId != current.itemId || !binding.searchPanel.isVisible) {
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

    private fun subtitleTimingKey(current: ResolvedPlayback): String? {
        if (current.isLive) return null
        val track = MediaTracks.selectedSidecar(current.subtitleTracks, current.selectedSubtitleIndex) ?: return null
        if (track.codec?.lowercase() !in setOf("subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "ttml")) return null
        return SubtitleTiming.key(current.serverAddress, current.userId, current.itemId, current.mediaSourceId, track.index, track.identity)
    }

    private fun subtitleSyncAction(start: Boolean, explicit: Boolean = false) {
        val current = playback ?: return
        subtitleStatusItem = current.itemId
        subtitleSyncJob?.cancel()
        subtitleSyncJob = lifecycleScope.launch {
            var showDetails = explicit
            if (start) {
                val accepted = withContext(Dispatchers.IO) { runCatching { SubtitleSync.start(current, ignoreSsl) } }
                if (!isActiveSafe() || playback?.itemId != current.itemId) return@launch
                if (accepted.isFailure) {
                    trackDrawer.setJobStatus(accepted.exceptionOrNull()?.message ?: getString(R.string.track_panel_job_failed))
                    return@launch
                }
            }
            do {
                val result = withContext(Dispatchers.IO) { runCatching { SubtitleSync.readStatus(current, ignoreSsl) } }
                if (!isActiveSafe() || playback?.itemId != current.itemId) return@launch
                val status = result.getOrNull()
                if (status == null) {
                    if (start || explicit) trackDrawer.setJobStatus(result.exceptionOrNull()?.message ?: getString(R.string.track_panel_job_failed))
                    return@launch
                }
                val brief = when {
                    status.state == "completed" -> getString(R.string.track_panel_job_done)
                    status.active -> getString(R.string.track_panel_job_running, status.percent)
                    status.exists -> getString(R.string.track_panel_job_attention)
                    else -> ""
                }
                trackDrawer.setJobStatus(brief)
                if (showDetails) { Toast.makeText(this@PlayerActivity, status.message, Toast.LENGTH_LONG).show(); showDetails = false }
                binding.osdSubtitleJob.text = brief
                binding.osdSubtitleJob.isVisible = status.exists
                if (!status.active) break
                delay(5_000)
            } while (true)
        }
    }

    private fun refreshSubtitleTracks() {
        val current = playback ?: return
        val payload = originalPayload ?: return
        lifecycleScope.launch {
            val refreshed = withContext(Dispatchers.IO) { runCatching {
                StreamResolver.resolve(payload, ignoreSsl, current.selectedAudioIndex, current.selectedSubtitleIndex)
            }.getOrNull() }
            if (!isActiveSafe() || playback !== current) return@launch
            if (refreshed == null) { Toast.makeText(this@PlayerActivity, R.string.subtitle_download_failed, Toast.LENGTH_LONG).show(); return@launch }
            // New sidecars renumber public stream indexes; retain the actual selected identities.
            val subtitle = current.subtitleTracks.firstOrNull { it.index == current.selectedSubtitleIndex }
            val audio = current.audioTracks.firstOrNull { it.index == current.selectedAudioIndex }
            playback = current.copy(subtitleTracks = refreshed.subtitleTracks, audioTracks = refreshed.audioTracks,
                selectedSubtitleIndex = subtitle?.let { old -> refreshed.subtitleTracks.firstOrNull { it.identity == old.identity }?.index } ?: -1,
                selectedAudioIndex = audio?.let { old -> refreshed.audioTracks.firstOrNull { it.identity == old.identity }?.index } ?: refreshed.selectedAudioIndex)
            renderTrackPanel()
            Toast.makeText(this@PlayerActivity, R.string.track_panel_refreshed, Toast.LENGTH_SHORT).show()
        }
    }

    private fun adjustSubtitleOffset(deltaMs: Long) {
        val current = playback ?: return
        val key = subtitleTimingKey(current) ?: return
        subtitleOffsetMs = SubtitleTiming.clamp(subtitleOffsetMs + deltaMs)
        trackDrawer.updateOffset(subtitleOffsetMs)
        getSharedPreferences("subtitle-timing", MODE_PRIVATE).edit().putLong(key, subtitleOffsetMs).apply()
        pendingSubtitleOffset?.let(mainHandler::removeCallbacks)
        // Coalesce repeated remote presses. Re-prepare at the current position so already
        // decoded cues are invalidated too; the playback session and pause state stay intact.
        pendingSubtitleOffset = Runnable {
            val exo = player ?: return@Runnable
            if (playback?.let(::subtitleTimingKey) != key) return@Runnable
            val factory = mediaSourceFactory ?: return@Runnable
            val position = exo.currentPosition
            val playing = exo.playWhenReady
            factory.setSubtitleParserFactory(OffsetSubtitleParserFactory(subtitleOffsetMs))
            exo.setMediaSource(factory.createMediaSource(mediaItemFor(current)), position)
            exo.prepare()
            exo.playWhenReady = playing
            Log.i("FireTvPlayback", "subtitle offset_ms=$subtitleOffsetMs position_ms=$position paused=${!playing}")
        }.also { mainHandler.postDelayed(it, 450) }
    }

    private fun applyRemoteSubtitle(item: RemoteSubtitle) {
        val current = playback ?: return
        val payload = originalPayload ?: return
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
            if (!isActiveSafe() || resolved == null || playback?.itemId != current.itemId) {
                binding.searchStatus.setText(R.string.subtitle_download_failed)
                return@launch
            }
            val added = resolved.subtitleTracks.firstOrNull { candidate ->
                candidate.isExternal && current.subtitleTracks.none { it.identity == candidate.identity }
            } ?: resolved.subtitleTracks.firstOrNull { it.isExternal && it.language.equals(item.language, true) }
            val position = player?.currentPosition ?: current.startPositionMs
            val playing = player?.playWhenReady ?: true
            Toast.makeText(this@PlayerActivity, R.string.subtitle_applied, Toast.LENGTH_SHORT).show()
            binding.searchPanel.isVisible = false
            startPlayer(
                resolved.copy(selectedSubtitleIndex = added?.index ?: resolved.selectedSubtitleIndex),
                position,
            )
            player?.playWhenReady = playing
            trackDrawer.subtitles()
            showTrackPanel()
        }
    }

    override fun pause() {
        desiredPlayWhenReady = false
        player?.pause()
        showOsd()
    }

    override fun resume() {
        desiredPlayWhenReady = true
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

    private fun refreshLiveGuide(current: ResolvedPlayback) {
        val now = SystemClock.elapsedRealtime()
        if (now < nextGuideAt || guideJob?.isActive == true) return
        nextGuideAt = now + 60_000
        val channel = LiveTvChannel(current.itemId, liveChannelTitle() ?: current.title)
        guideJob = lifecycleScope.launch {
            val channel = withContext(Dispatchers.IO) {
                runCatching {
                    val at = java.time.Instant.now()
                    val query = "userId=${current.userId}&channelIds=${current.itemId}" +
                        "&minEndDate=${Uri.encode(at.toString())}&maxStartDate=${Uri.encode(at.plusSeconds(43_200).toString())}" +
                        "&sortBy=StartDate&sortOrder=Ascending&enableImages=false&enableUserData=false&limit=64"
                    val response = JellyfinHttp.get(
                        "${current.serverAddress}/LiveTv/Programs?$query",
                        current.accessToken, ignoreSsl, connectTimeoutMs = 4_000, readTimeoutMs = 4_000,
                    )
                    if (response.code in 200..299) LiveTvChannels.withPrograms(channel, response.body, at.toEpochMilli()) else null
                }.getOrNull()
            }
            if (playback === current && channel != null) liveGuideChannel = channel
        }
    }

    private fun stopAndClose() {
        tune.invalidate()
        cancelPendingTune()
        emitSync("playbackstop")
        retirePlayback(keepPlayer = false)
        finish()
    }

    private fun retirePlayback(keepPlayer: Boolean, failed: Boolean = false) {
        guideJob?.cancel()
        metadataHttp?.cancel()
        metadataHttp = null
        metadataJob?.cancel()
        mainHandler.removeCallbacks(showRebuffering)
        binding.rebuffering.isVisible = false
        val exo = player
        val position = exo?.currentPosition ?: 0L
        val closing = playback
        val closingReporter = reporter
        val ssl = ignoreSsl
        playerListener?.let { exo?.removeListener(it) }
        playerListener = null
        playback = null
        reporter = null
        mainHandler.removeCallbacks(progressTick)
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.removeCallbacks(osdTick)
        mainHandler.removeCallbacks(stallWatchdog)
        if (keepPlayer) {
            exo?.stop()
            exo?.clearMediaItems()
        } else releasePlayer()
        if (closing != null) {
            // Playing/Stopped already releases this consumer on the server. Closing it
            // twice can kill a new tune or another viewer sharing the same stream.
            // Its completion outlives the Activity and gates the next resolve.
            cleanupFuture = closingReporter?.stopped(position, failed) ?: cleanupExecutor.submit {
                StreamResolver.closeLiveStream(closing, ssl)
            }
        }
    }

    private fun releasePlayer(clearPlayback: Boolean = true) {
        playerListener?.let { player?.removeListener(it) }
        playerListener = null
        mainHandler.removeCallbacks(progressTick)
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.removeCallbacks(osdTick)
        mainHandler.removeCallbacks(stallWatchdog)
        binding.playerView.player = null
        player?.release()
        player = null
        if (clearPlayback) playback = null
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
            retirePlayback(keepPlayer = false)
        } else if (player?.isPlaying == true) {
            player?.pause()
            reporter?.progress(player?.currentPosition ?: 0L, true)
            pausedBySystem = true
        }
    }

    override fun onDestroy() {
        cancelServerChoices()
        subtitleSyncJob?.cancel()
        tune.invalidate()
        cancelPendingTune()
        if (PlayerCommands.listener === this) {
            PlayerCommands.listener = null
        }
        retirePlayback(keepPlayer = false)
        super.onDestroy()
    }

    companion object {
        const val EXTRA_PAYLOAD = "payload"
        const val EXTRA_PAYLOAD_ID = "payload_id"
        const val EXTRA_IGNORE_SSL = "ignore_ssl"
        private val resolverExecutor = Executors.newFixedThreadPool(2)
        private val cleanupExecutor = Executors.newCachedThreadPool()
    }
}
