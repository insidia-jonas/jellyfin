using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Channels
{
    /// <summary>
    /// Channel that overlays volatile presentation onto existing folder items
    /// without changing the ChannelManager cache identity.
    /// </summary>
    public interface IChannelPresentationOverlay
    {
        /// <summary>
        /// Overlays presentation fields onto the current folder items.
        /// </summary>
        /// <param name="items">The library items for the open folder.</param>
        void OverlayPresentation(IReadOnlyList<BaseItem> items);
    }
}
