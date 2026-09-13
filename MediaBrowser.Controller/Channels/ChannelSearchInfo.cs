#nullable disable

namespace MediaBrowser.Controller.Channels
{
    /// <summary>
    /// The request for a search in a channel.
    /// </summary>
    public class ChannelSearchInfo
    {
        /// <summary>
        /// Gets or sets the search term.
        /// </summary>
        public string SearchTerm { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of title cards to return.
        /// </summary>
        public int? Limit { get; set; }
    }
}
