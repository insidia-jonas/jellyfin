using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Opens Live TV IChannel items through the same tuner path as the official guide.
/// </summary>
internal static class LiveTvLibraryChannelPlayback
{
    /// <summary>
    /// Sets <see cref="MediaSourceInfo.OpenToken"/> so PlaybackInfo can open the tuner stream.
    /// Without this, AutoOpenLiveStream calls OpenMediaSource with an empty token and fails.
    /// </summary>
    /// <param name="sources">Media sources from a tuner host.</param>
    /// <param name="channelId">The tuner channel id.</param>
    /// <returns>The same sources, with open tokens assigned.</returns>
    public static IReadOnlyList<MediaSourceInfo> PrepareMediaSources(
        IEnumerable<MediaSourceInfo> sources,
        string channelId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var list = sources as IList<MediaSourceInfo> ?? sources.ToList();
        foreach (var source in list)
        {
            if (string.IsNullOrEmpty(source.OpenToken))
            {
                source.OpenToken = channelId;
            }
        }

        return list as IReadOnlyList<MediaSourceInfo> ?? list.ToArray();
    }

    /// <summary>
    /// Opens a tuner live stream for a Live TV library-channel item.
    /// </summary>
    /// <param name="hosts">Registered tuner hosts.</param>
    /// <param name="channelId">The tuner channel id (also used as the stream share key).</param>
    /// <param name="currentLiveStreams">Already-open live streams.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The opened live stream.</returns>
    public static async Task<ILiveStream> OpenAsync(
        IReadOnlyList<ITunerHost> hosts,
        string channelId,
        IList<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentException.ThrowIfNullOrEmpty(channelId);

        var shared = currentLiveStreams?.FirstOrDefault(stream =>
            string.Equals(stream.OriginalStreamId, channelId, StringComparison.OrdinalIgnoreCase)
            && stream.EnableStreamSharing);
        if (shared is not null)
        {
            shared.ConsumerCount++;
            return shared;
        }

        foreach (var host in hosts)
        {
            try
            {
                var liveStream = await host
                    .GetChannelStream(channelId, channelId, currentLiveStreams ?? [], cancellationToken)
                    .ConfigureAwait(false);
                liveStream.OriginalStreamId = channelId;
                EnsureLiveStreamId(liveStream, channelId);
                return liveStream;
            }
            catch (FileNotFoundException)
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        throw new ResourceNotFoundException($"Unable to open Live TV channel {channelId}");
    }

    /// <summary>
    /// MediaSourceManager indexes open streams by LiveStreamId. Tuner hosts do not set it
    /// (the official Live TV provider does), so IChannel playback must assign one.
    /// </summary>
    /// <param name="liveStream">The opened tuner stream.</param>
    /// <param name="channelId">Fallback id when the media source has none.</param>
    internal static void EnsureLiveStreamId(ILiveStream liveStream, string channelId)
    {
        ArgumentNullException.ThrowIfNull(liveStream);
        var source = liveStream.MediaSource;
        if (source is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(source.LiveStreamId))
        {
            source.LiveStreamId = string.IsNullOrEmpty(source.Id) ? channelId : source.Id;
        }
    }
}
