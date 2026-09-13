using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Groups SABnzbd / Downloads rows so one movie is not listed three times.
/// </summary>
public static class DownloadList
{
    /// <summary>
    /// Keeps one row per title. Active downloads win over failed, failed over completed.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="items">The rows.</param>
    /// <param name="titleSelector">The display title.</param>
    /// <param name="statusSelector">The SABnzbd status.</param>
    /// <returns>Deduped rows, original order of the winning row.</returns>
    public static IReadOnlyList<T> DedupeByTitle<T>(
        IEnumerable<T> items,
        Func<T, string?> titleSelector,
        Func<T, string?> statusSelector)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var winners = new List<T>();
        foreach (var item in items)
        {
            var key = GrabService.NameKey(titleSelector(item));
            if (key.Length == 0)
            {
                winners.Add(item);
                continue;
            }

            if (!seen.TryGetValue(key, out var index))
            {
                seen[key] = winners.Count;
                winners.Add(item);
                continue;
            }

            if (StatusPriority(statusSelector(item)) < StatusPriority(statusSelector(winners[index])))
            {
                winners[index] = item;
            }
        }

        return winners;
    }

    /// <summary>
    /// Sort key for a SABnzbd status: 0 = still running, 1 = failed, 2 = completed.
    /// </summary>
    /// <param name="status">The status string.</param>
    /// <returns>The priority (lower is preferred when several rows share a title).</returns>
    public static int StatusPriority(string? status)
    {
        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 0;
    }
}
