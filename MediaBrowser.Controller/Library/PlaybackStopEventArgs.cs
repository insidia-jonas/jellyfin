#pragma warning disable CS1591

namespace MediaBrowser.Controller.Library
{
    public class PlaybackStopEventArgs : PlaybackProgressEventArgs
    {
        /// <summary>
        /// Gets or sets a value indicating whether [played to completion].
        /// </summary>
        /// <value><c>true</c> if [played to completion]; otherwise, <c>false</c>.</value>
        public bool PlayedToCompletion { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the client stopped because playback failed.
        /// </summary>
        public bool Failed { get; set; }
    }
}
