using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Maps imported guide programs onto tuner channel ids.
/// </summary>
internal static class LiveTvNowNextMap
{
    /// <summary>
    /// Builds now/next entries keyed by <see cref="ChannelInfo.Id"/> (the tuner external id).
    /// </summary>
    /// <param name="liveTvChannels">Library Live TV channel items.</param>
    /// <param name="programs">Imported guide programs.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>Now/next by tuner channel id.</returns>
    public static IReadOnlyDictionary<string, LiveTvNowNext> Create(
        IEnumerable<BaseItem> liveTvChannels,
        IEnumerable<LiveTvProgram> programs,
        DateTime utcNow)
    {
        var externalIdByInternal = liveTvChannels
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(static item => item.Id)
            .ToDictionary(static group => group.Key, static group => group.First().ExternalId);

        var byTunerId = new Dictionary<string, LiveTvNowNext>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in programs.Where(static p => p is not null).GroupBy(static p => p.ChannelId))
        {
            if (!externalIdByInternal.TryGetValue(group.Key, out var tunerId)
                || string.IsNullOrWhiteSpace(tunerId))
            {
                continue;
            }

            var ordered = group.OrderBy(static p => p.StartDate).ToList();
            var now = ordered.FirstOrDefault(p => p.StartDate <= utcNow && p.EndDate > utcNow);
            var next = ordered.FirstOrDefault(p => p.StartDate > utcNow);
            if (now is null && next is null)
            {
                continue;
            }

            byTunerId[tunerId] = new LiveTvNowNext
            {
                NowTitle = now?.Name,
                NowStart = now?.StartDate,
                NowEnd = now?.EndDate,
                NowOverview = now?.Overview,
                NowImageUrl = now?.PrimaryImagePath,
                NextTitle = next?.Name,
                NextStart = next?.StartDate,
                NextEnd = next?.EndDate
            };
        }

        return byTunerId;
    }
}
