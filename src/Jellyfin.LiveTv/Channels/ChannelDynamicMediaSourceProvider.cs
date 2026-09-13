using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;

namespace Jellyfin.LiveTv.Channels
{
    /// <summary>
    /// A media source provider for channels.
    /// </summary>
    public class ChannelDynamicMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ChannelManager _channelManager;
        private readonly ITunerHostManager _tunerHostManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelDynamicMediaSourceProvider"/> class.
        /// </summary>
        /// <param name="channelManager">The channel manager.</param>
        /// <param name="tunerHostManager">The tuner host manager.</param>
        public ChannelDynamicMediaSourceProvider(IChannelManager channelManager, ITunerHostManager tunerHostManager)
        {
            _channelManager = (ChannelManager)channelManager;
            _tunerHostManager = tunerHostManager;
        }

        /// <inheritdoc />
        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            return item.SourceType == SourceType.Channel
                ? _channelManager.GetDynamicMediaSources(item, cancellationToken)
                : Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            if (!LiveTvLibraryChannelPlayback.IsTunerChannelId(openToken))
            {
                throw new FileNotFoundException();
            }

            return LiveTvLibraryChannelPlayback.OpenAsync(
                _tunerHostManager.TunerHosts,
                openToken,
                currentLiveStreams,
                cancellationToken);
        }
    }
}
