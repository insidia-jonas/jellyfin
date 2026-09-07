#nullable disable
#pragma warning disable CS1591

namespace MediaBrowser.Model.LiveTv
{
    public class TunerHostInfo
    {
        public TunerHostInfo()
        {
            AllowHWTranscoding = true;
            IgnoreDts = true;
            ReadAtNativeFramerate = false;
            AllowStreamSharing = true;
            AllowFmp4TranscodingContainer = false;
            FallbackMaxStreamingBitrate = 30000000;
            AlternateUrls = [];
            HangTimeoutSeconds = 10;
        }

        public string Id { get; set; }

        public string Url { get; set; }

        public string Type { get; set; }

        public string DeviceId { get; set; }

        public string FriendlyName { get; set; }

        public bool ImportFavoritesOnly { get; set; }

        public bool AllowHWTranscoding { get; set; }

        public bool AllowFmp4TranscodingContainer { get; set; }

        public bool AllowStreamSharing { get; set; }

        public int FallbackMaxStreamingBitrate { get; set; }

        public bool EnableStreamLooping { get; set; }

        public string Source { get; set; }

        public int TunerCount { get; set; }

        public string UserAgent { get; set; }

        /// <summary>
        /// Gets or sets an HTTP Referer sent with playlist and stream requests.
        /// </summary>
        public string Referrer { get; set; }

        /// <summary>
        /// Gets or sets an XMLTV/EPG URL imported from the M3U header or set by the user.
        /// </summary>
        public string EpgUrl { get; set; }

        /// <summary>
        /// Gets or sets additional playlist URLs (other ingest servers) used for health scoring and failover.
        /// The primary <see cref="Url"/> field may also contain pipe-separated URLs.
        /// </summary>
        public string[] AlternateUrls { get; set; }

        /// <summary>
        /// Gets or sets the playlist URL currently selected by health checks.
        /// </summary>
        public string ActiveUrl { get; set; }

        /// <summary>
        /// Gets or sets how many seconds without data before a live stream is treated as hung.
        /// </summary>
        public int HangTimeoutSeconds { get; set; }

        public bool IgnoreDts { get; set; }

        public bool ReadAtNativeFramerate { get; set; }
    }
}
