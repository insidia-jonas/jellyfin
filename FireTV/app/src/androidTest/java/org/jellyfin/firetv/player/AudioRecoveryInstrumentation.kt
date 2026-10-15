package org.jellyfin.firetv.player

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.os.Bundle
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlaybackException
import androidx.media3.exoplayer.ExoPlayer
import org.jellyfin.firetv.core.ResolvedPlayback
import org.jellyfin.firetv.core.MediaTrack
import java.io.File

/** Opt-in device test. Supply a private playback payload; no credentials belong in the APK. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
class AudioRecoveryInstrumentation : Instrumentation() {
    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        start()
    }

    private fun field(activity: PlayerActivity, name: String): Any? =
        PlayerActivity::class.java.getDeclaredField(name).apply { isAccessible = true }.get(activity)

    private fun awaitMain(check: () -> Boolean) {
        val until = System.currentTimeMillis() + 40_000
        while (System.currentTimeMillis() < until) {
            var passed = false
            runOnMainSync { passed = check() }
            if (passed) return
            Thread.sleep(200)
        }
        error("Playback condition timed out")
    }

    override fun onStart() {
        val result = Bundle()
        var activity: PlayerActivity? = null
        var success = false
        try {
            val file = File(targetContext.filesDir, "audio-recovery-test.json")
            val payload = file.readText()
            check(file.delete())
            val current = startActivitySync(Intent(targetContext, PlayerActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK).putExtra(PlayerActivity.EXTRA_PAYLOAD, payload)) as PlayerActivity
            activity = current
            awaitMain { (field(current, "player") as? ExoPlayer)?.isPlaying == true }
            var before = 0L
            runOnMainSync {
                before = (field(current, "player") as ExoPlayer).currentPosition
                val listener = field(current, "playerListener") as Player.Listener
                listener.onPlayerError(ExoPlaybackException.createForRenderer(
                    IllegalStateException("Injected audio decoder failure"), "MediaCodecAudioRenderer", 1,
                    Format.Builder().setSampleMimeType(MimeTypes.AUDIO_E_AC3).build(),
                    C.FORMAT_UNSUPPORTED_TYPE, false, PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED))
            }
            awaitMain {
                (field(current, "playback") as? ResolvedPlayback)?.playMethod == "Transcode" &&
                    (field(current, "player") as? ExoPlayer)?.isPlaying == true
            }
            Thread.sleep(3500)
            runOnMainSync {
                val player = field(current, "player") as ExoPlayer
                check(player.currentPosition in before..(before + 15_000)) { "Recovery lost the playback position" }
                check(player.playerError == null) { "Compatible audio failed on the device" }
                result.putLong("beforeMs", before)
                result.putLong("afterMs", player.currentPosition)
                result.putString("audioMime", player.audioFormat?.sampleMimeType)
                result.putString("videoMime", player.videoFormat?.sampleMimeType)
                check(player.audioFormat?.sampleMimeType == MimeTypes.AUDIO_AAC)
            }
            var pausedAt = 0L
            var otherIndex = -1
            runOnMainSync {
                val player = field(current, "player") as ExoPlayer
                player.pause()
                pausedAt = player.currentPosition
                val resolved = field(current, "playback") as ResolvedPlayback
                val other = resolved.audioTracks.firstOrNull { it.index != resolved.selectedAudioIndex }
                check(other != null) { "Device fixture needs two audio tracks" }
                otherIndex = other.index
                PlayerActivity::class.java.getDeclaredMethod("onAudioPicked", MediaTrack::class.java)
                    .apply { isAccessible = true }.invoke(current, other)
            }
            awaitMain {
                val resolved = field(current, "playback") as? ResolvedPlayback
                val player = field(current, "player") as? ExoPlayer
                resolved?.selectedAudioIndex == otherIndex && resolved.playMethod == "Transcode" &&
                    player?.playbackState == Player.STATE_READY
            }
            runOnMainSync {
                val player = field(current, "player") as ExoPlayer
                check(!player.playWhenReady)
                check(kotlin.math.abs(player.currentPosition - pausedAt) < 1000) { "Paused track change lost position" }
                result.putBoolean("pausedTrackChange", true)
                result.putInt("changedAudioIndex", otherIndex)
                player.play()
            }
            awaitMain { (field(current, "player") as? ExoPlayer)?.currentPosition?.let { it > pausedAt + 2500 } == true }
            success = true
            result.putString("stream", "Audio decoder recovery resumed using AAC on the device.\n")
        } catch (error: Throwable) {
            // Don't expose URLs or payloads in instrumentation output.
            result.putString("stream", "Audio recovery test failed: ${error.javaClass.simpleName}\n")
        } finally {
            activity?.let { runOnMainSync { it.finish() } }
            // Let the ordered stop report drain before instrumentation ends the process.
            Thread.sleep(2000)
            finish(if (success) Activity.RESULT_OK else Activity.RESULT_CANCELED, result)
        }
    }
}
