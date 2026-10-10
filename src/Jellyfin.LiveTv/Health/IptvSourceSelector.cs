using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv.Health;

/// <summary>Per-playback choices from the existing cached tuner catalogue.</summary>
public sealed class IptvSourceSelector : IIptvSourceSelector
{
    private readonly ITunerHostManager _tuners;
    private readonly IConfigurationManager _configuration;
    private readonly IptvWatchdog _watchdog;

    /// <summary>Initializes a new instance of the <see cref="IptvSourceSelector"/> class.</summary>
    /// <param name="tuners">Cached channels.</param>
    /// <param name="configuration">Configured origins.</param>
    /// <param name="watchdog">Media observations.</param>
    public IptvSourceSelector(ITunerHostManager tuners, IConfigurationManager configuration, IptvWatchdog watchdog)
    {
        _tuners = tuners;
        _configuration = configuration;
        _watchdog = watchdog;
    }

    /// <inheritdoc />
    public IptvChannelSources GetSources(string channelId, string? liveStreamId = null)
    {
        var found = Find(channelId);
        if (found is null)
        {
            return new IptvChannelSources();
        }

        var (host, tuner, channel) = found.Value;
        return _watchdog.GetChannelSources(tuner, channel.Id, host.CreateProbeSource(tuner, channel).Id, liveStreamId);
    }

    /// <inheritdoc />
    public MediaSourceInfo? Select(string channelId, IReadOnlyList<MediaSourceInfo> sources, string selectionId)
    {
        var found = Find(channelId);
        if (found is null)
        {
            return null;
        }

        var (host, tuner, channel) = found.Value;
        var baseId = host.CreateProbeSource(tuner, channel).Id;
        return SelectSource(tuner, baseId, sources, selectionId);
    }

    internal static MediaSourceInfo? SelectSource(TunerHostInfo tuner, string baseId, IReadOnlyList<MediaSourceInfo> sources, string selectionId)
    {
        var origin = IptvSourceChoice.Resolve(tuner, baseId, selectionId);
        var source = sources.FirstOrDefault(s => s.Id == baseId && s.RequiresOpening && s.IsInfiniteStream);
        if (origin is null || source?.OpenToken?.EndsWith("_" + baseId, StringComparison.Ordinal) != true)
        {
            return null;
        }

        // Never mutate an ordinary/cached source or the shared tuner's ActiveUrl.
        var selected = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(source))!;
        selected.Id = selectionId;
        selected.OpenToken = source.OpenToken[..^baseId.Length] + selectionId;
        selected.Path = M3uUrlFailover.RewriteStreamUrl(source.Path, origin);
        selected.Name = IptvSourceChoice.Name(origin);
        return selected;
    }

    private (M3UTunerHost Host, TunerHostInfo Tuner, ChannelInfo Channel)? Find(string channelId)
    {
        foreach (var host in _tuners.TunerHosts.OfType<M3UTunerHost>())
        {
            var channel = host.GetCachedChannels().FirstOrDefault(c => c.Id == channelId);
            var tuner = channel is null ? null : _configuration.GetLiveTvConfiguration().TunerHosts.FirstOrDefault(t => t.Id == channel.TunerHostId);
            if (channel is not null && tuner is not null)
            {
                return (host, tuner, channel);
            }
        }

        return null;
    }
}
