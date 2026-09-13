package org.jellyfin.firetv.core

/**
 * Device profile sent with PlaybackInfo / LiveStreams/Open.
 *
 * Aligned with Jellyfin 12.0 TV clients: AV1 fMP4 remux, VideoRotation,
 * and VobSub/dvdsub (external extract or burn-in). Keep in sync with
 * nativeshell.js [exoPlayerProfile].
 */
object DeviceProfile {
    val JSON: String = """
        {
          "Name": "Jellyfin Fire TV ExoPlayer",
          "MaxStreamingBitrate": 120000000,
          "MaxStaticBitrate": 100000000,
          "MusicStreamingTranscodingBitrate": 320000,
          "DirectPlayProfiles": [
            {"Container":"mp4,m4v,mov,mkv,webm,ts,mpegts,avi","Type":"Video","VideoCodec":"h264,hevc,vp8,vp9,av1,mpeg2video,mpeg4","AudioCodec":"aac,mp3,ac3,eac3,flac,opus,pcm,dts"},
            {"Container":"mp3,aac,flac,wav,ogg,opus,m4a","Type":"Audio"}
          ],
          "TranscodingProfiles": [
            {"Container":"mp4","Type":"Video","VideoCodec":"h264,hevc,av1","AudioCodec":"aac,ac3,eac3","Protocol":"http","Context":"Streaming","MaxAudioChannels":"8","CopyTimestamps":true},
            {"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac,ac3","Protocol":"hls","Context":"Streaming","MaxAudioChannels":"6","MinSegments":"1","BreakOnNonKeyFrames":true},
            {"Container":"mp3","Type":"Audio","AudioCodec":"mp3","Protocol":"http","Context":"Streaming"}
          ],
          "ContainerProfiles": [],
          "CodecProfiles": [
            {
              "Type": "Video",
              "Codec": "h264",
              "Conditions": [
                {"Condition":"EqualsAny","Property":"VideoProfile","Value":"high|main|baseline|constrained baseline","IsRequired":false},
                {"Condition":"LessThanEqual","Property":"VideoLevel","Value":"51","IsRequired":false},
                {"Condition":"EqualsAny","Property":"VideoRotation","Value":"0|90|180|270","IsRequired":false}
              ]
            },
            {
              "Type": "Video",
              "Conditions": [
                {"Condition":"EqualsAny","Property":"VideoRotation","Value":"0|90|180|270","IsRequired":false}
              ]
            }
          ],
          "SubtitleProfiles": [
            {"Format":"vtt","Method":"External"},
            {"Format":"srt","Method":"External"},
            {"Format":"subrip","Method":"External"},
            {"Format":"ttml","Method":"External"},
            {"Format":"ass","Method":"External"},
            {"Format":"vobsub","Method":"External"},
            {"Format":"dvdsub","Method":"External"},
            {"Format":"ssa","Method":"Encode"},
            {"Format":"pgssub","Method":"Encode"},
            {"Format":"vobsub","Method":"Encode"},
            {"Format":"dvdsub","Method":"Encode"}
          ],
          "ResponseProfiles": []
        }
    """.trimIndent()
}
