package org.jellyfin.firetv.player

import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.util.Consumer
import androidx.media3.extractor.text.CuesWithTiming
import androidx.media3.extractor.text.DefaultSubtitleParserFactory
import androidx.media3.extractor.text.SubtitleParser

/** Shift decoded sidecar cues, including ASS styling, rather than the audio/video clock. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
class OffsetSubtitleParserFactory(private val offsetMs: Long) : SubtitleParser.Factory {
    private val factory = DefaultSubtitleParserFactory()
    override fun supportsFormat(format: Format) = factory.supportsFormat(format)
    override fun getCueReplacementBehavior(format: Format) = factory.getCueReplacementBehavior(format)
    override fun create(format: Format): SubtitleParser {
        val parser = factory.create(format)
        return object : SubtitleParser by parser {
            override fun parse(data: ByteArray, offset: Int, length: Int, options: SubtitleParser.OutputOptions, output: Consumer<CuesWithTiming>) {
                val delta = offsetMs * 1000
                // Full sidecars are small. Keep cues that began before a paused seek
                // position too, otherwise applying an offset drops the current line.
                parser.parse(data, offset, length, SubtitleParser.OutputOptions.allCues()) { cue ->
                    // Container-relative samples have no independent timestamp; the selected
                    // text track is delivered as a complete, absolute-timed sidecar instead.
                    output.accept(if (cue.startTimeUs == C.TIME_UNSET) cue else CuesWithTiming(cue.cues, cue.startTimeUs + delta, cue.durationUs))
                }
            }
        }
    }
}
