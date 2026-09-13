using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Curated list of common movie/TV genres. The indexer's caps expose thousands of raw library
/// tags (including adult ones), so both the channel's genre folders and the Browse page selector
/// only surface this clean whitelist — nothing pornographic can appear.
/// </summary>
public static class CommonGenres
{
    /// <summary>Gets the curated common genre names, in display order.</summary>
    public static readonly string[] Names =
    {
        "Action", "Adventure", "Animation", "Comedy", "Crime", "Documentary", "Drama",
        "Family", "Fantasy", "History", "Horror", "Music", "Musical", "Mystery",
        "Romance", "Science Fiction", "Sci-Fi", "Thriller", "War", "Western"
    };

    /// <summary>
    /// Intersects the whitelist with the genres the indexer actually knows, preserving the
    /// whitelist order. Falls back to the full whitelist when the available set is empty.
    /// </summary>
    /// <param name="available">The genre names reported by the indexer.</param>
    /// <returns>The common genres to display.</returns>
    public static List<string> FilterAvailable(IEnumerable<string?> available)
    {
        var set = new HashSet<string>(
            available.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!),
            StringComparer.OrdinalIgnoreCase);

        var result = Names.Where(set.Contains).ToList();
        return result.Count > 0 ? result : Names.ToList();
    }
}
