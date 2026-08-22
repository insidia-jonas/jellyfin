package org.jellyfin.firetv.player

import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.jellyfin.firetv.R
import org.jellyfin.firetv.databinding.ActivityPlayerBinding
import java.util.Locale
import java.util.concurrent.TimeUnit

class PlayerActivity : AppCompatActivity(), PlayerCommands.Listener {
    private lateinit var binding: ActivityPlayerBinding
    private var player: ExoPlayer? = null
    private var playback: ResolvedPlayback? = null
    private var reporter: PlaybackReporter? = null
    private val mainHandler = Handler(Looper.getMainLooper())
    private val hideOsd = Runnable { binding.osd.isVisible = false }
    private val progressTick = object : Runnable {
        override fun run() {
            val exo = player ?: return
            updateOsd()
            val current = playback
            if (current != null) {
                lifecycleScope.launch(Dispatchers.IO) {
                    reporter?.progress(exo.currentPosition, !exo.isPlaying)
                }
            }
            mainHandler.postDelayed(this, 10_000)
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
        PlayerCommands.listener = this

        val payload = intent.getStringExtra(EXTRA_PAYLOAD)
        if (payload.isNullOrBlank()) {
            finish()
            return
        }
        val ignoreSsl = intent.getBooleanExtra(EXTRA_IGNORE_SSL, false)
        lifecycleScope.launch {
            val resolved = withContext(Dispatchers.IO) {
                runCatching { StreamResolver.resolve(payload, ignoreSsl) }
            }
            val playback = resolved.getOrNull()
            if (playback == null) {
                Toast.makeText(
                    this@PlayerActivity,
                    resolved.exceptionOrNull()?.message ?: getString(R.string.playback_failed),
                    Toast.LENGTH_LONG,
                ).show()
                finish()
                return@launch
            }
            startPlayer(playback)
        }
    }

    private fun startPlayer(resolved: ResolvedPlayback) {
        playback = resolved
        reporter = PlaybackReporter(resolved)
        binding.osdTitle.text = resolved.title
        binding.loadingTitle.text = resolved.title
        val exo = ExoPlayer.Builder(this).build().also { player = it }
        binding.playerView.player = exo
        binding.playerView.useController = false
        exo.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_READY) {
                    binding.loading.isVisible = false
                }
                if (playbackState == Player.STATE_ENDED) {
                    stopAndClose()
                }
            }

            override fun onPlayerError(error: PlaybackException) {
                Toast.makeText(this@PlayerActivity, getString(R.string.playback_failed), Toast.LENGTH_LONG).show()
                stopAndClose()
            }
        })
        exo.setMediaItem(MediaItem.fromUri(resolved.url))
        exo.prepare()
        if (resolved.startPositionMs > 0) {
            exo.seekTo(resolved.startPositionMs)
        }
        exo.playWhenReady = true
        lifecycleScope.launch(Dispatchers.IO) { reporter?.playing() }
        mainHandler.post(progressTick)
        showOsd()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action != KeyEvent.ACTION_DOWN) {
            return super.dispatchKeyEvent(event)
        }
        val exo = player ?: return super.dispatchKeyEvent(event)
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
                exo.seekTo((exo.currentPosition + 30_000).coerceAtMost(exo.duration.coerceAtLeast(0)))
                showOsd()
                true
            }
            KeyEvent.KEYCODE_DPAD_LEFT,
            KeyEvent.KEYCODE_MEDIA_REWIND,
            -> {
                exo.seekTo((exo.currentPosition - 10_000).coerceAtLeast(0))
                showOsd()
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

    private fun showOsd() {
        binding.osd.isVisible = true
        updateOsd()
        mainHandler.removeCallbacks(hideOsd)
        mainHandler.postDelayed(hideOsd, 4_000)
    }

    private fun updateOsd() {
        val exo = player ?: return
        val duration = exo.duration.coerceAtLeast(0)
        val position = exo.currentPosition.coerceAtLeast(0)
        binding.osdSeek.max = 1000
        binding.osdSeek.progress = if (duration > 0) ((position * 1000) / duration).toInt() else 0
        binding.osdTime.text = "${formatTime(position)} / ${formatTime(duration)}"
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
        player?.seekTo(positionMs.coerceAtLeast(0))
        showOsd()
    }

    override fun setVolume(percent: Int) = Unit

    override fun destroy() {
        stopAndClose()
    }

    private fun stopAndClose() {
        val exo = player
        val position = exo?.currentPosition ?: 0L
        lifecycleScope.launch(Dispatchers.IO) {
            reporter?.stopped(position)
        }
        releasePlayer()
        finish()
    }

    private fun releasePlayer() {
        mainHandler.removeCallbacks(progressTick)
        mainHandler.removeCallbacks(hideOsd)
        binding.playerView.player = null
        player?.release()
        player = null
    }

    override fun onStop() {
        super.onStop()
        if (isFinishing) {
            releasePlayer()
        }
    }

    override fun onDestroy() {
        if (PlayerCommands.listener === this) {
            PlayerCommands.listener = null
        }
        releasePlayer()
        super.onDestroy()
    }

    companion object {
        const val EXTRA_PAYLOAD = "payload"
        const val EXTRA_IGNORE_SSL = "ignore_ssl"
    }
}
