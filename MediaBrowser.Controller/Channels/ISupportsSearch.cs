using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Channels
{
    /// <summary>
    /// Interface for channels that can answer a free-text search (native Search/Hints).
    /// </summary>
    public interface ISupportsSearch
    {
        /// <summary>
        /// Gets channel items matching the search term.
        /// </summary>
        /// <param name="searchInfo">The search request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Matching channel items (title cards, not play-to-download clips).</returns>
        Task<IEnumerable<ChannelItemInfo>> GetSearchResults(ChannelSearchInfo searchInfo, CancellationToken cancellationToken);
    }
}
