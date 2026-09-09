using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Paging and newest-first ordering for category folders (Movies, TV, DE, genre).
/// The indexer returns releases (qualities); after grouping, a 200-row NZB page often
/// collapses to ~25 titles — so we keep fetching until enough unique titles exist.
/// </summary>
public static class CategoryBrowse
{
    /// <summary>How many unique titles a category tries to collect.</summary>
    public const int DefaultTitleLimit = 200;

    /// <summary>Title cards per folder page (Fire TV rarely paginates a single grid).</summary>
    public const int CardsPerPage = 50;

    /// <summary>Max indexer pages of 100 releases each.</summary>
    public const int MaxFetchPages = 8;

    /// <summary>
    /// Counts unique movie/show keys in a release list.
    /// </summary>
    /// <param name="releases">The releases.</param>
    /// <returns>The unique title count.</returns>
    public static int CountUniqueKeys(IEnumerable<Release> releases)
        => releases
            .Where(static r => r is not null && !string.IsNullOrWhiteSpace(r.Guid))
            .Select(ReleaseGrouper.KeyOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    /// <summary>
    /// Newest posted title first, then preferred-language rank, then name.
    /// </summary>
    /// <param name="groups">The groups.</param>
    /// <param name="languageRank">Best language rank per group key (lower is better).</param>
    /// <returns>The ordered groups.</returns>
    public static List<ReleaseGroup> OrderNewest(
        IEnumerable<ReleaseGroup> groups,
        IReadOnlyDictionary<string, int> languageRank)
    {
        return groups
            .OrderByDescending(g => g.Posted ?? DateTimeOffset.MinValue)
            .ThenBy(g => languageRank.TryGetValue(g.Key, out var rank) ? rank : int.MaxValue)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Slices a 1-based page out of an ordered title list.
    /// </summary>
    /// <param name="items">The full ordered list.</param>
    /// <param name="page">The 1-based page.</param>
    /// <param name="pageSize">The page size.</param>
    /// <returns>The page items and the total page count.</returns>
    public static (IReadOnlyList<T> Page, int TotalPages) Slice<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)pageSize));
        if (page > totalPages)
        {
            return (Array.Empty<T>(), totalPages);
        }

        return (items.Skip((page - 1) * pageSize).Take(pageSize).ToList(), totalPages);
    }

    /// <summary>
    /// Builds the <c>pg:{scope}:{page}</c> folder id.
    /// </summary>
    /// <param name="scope">The category scope (<c>movies</c>, <c>genre:Action</c>, …).</param>
    /// <param name="page">The 1-based page.</param>
    /// <returns>The folder id (without the channel prefix).</returns>
    public static string PageFolderId(string scope, int page)
        => "pg:" + scope + ":" + page.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a <c>pg:{scope}:{page}</c> id.
    /// </summary>
    /// <param name="folderId">The folder id.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="page">The page.</param>
    /// <returns>True when the id is a category page folder.</returns>
    public static bool TryParsePageFolder(string folderId, out string scope, out int page)
    {
        scope = string.Empty;
        page = 0;
        if (string.IsNullOrWhiteSpace(folderId) || !folderId.StartsWith("pg:", StringComparison.Ordinal))
        {
            return false;
        }

        var last = folderId.LastIndexOf(':');
        if (last <= 3 || last == folderId.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(folderId.AsSpan(last + 1), out page) || page < 1)
        {
            return false;
        }

        scope = folderId[3..last];
        return !string.IsNullOrWhiteSpace(scope);
    }

    /// <summary>
    /// Label for a page folder, e.g. <c>Page 2 (51–100)</c>.
    /// </summary>
    /// <param name="page">The 1-based page.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="total">The total title count.</param>
    /// <returns>The label.</returns>
    public static string PageLabel(int page, int pageSize, int total)
    {
        var start = ((page - 1) * pageSize) + 1;
        var end = Math.Min(page * pageSize, total);
        return "Page " + page.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + " (" + start.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + "–" + end.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    }
}
