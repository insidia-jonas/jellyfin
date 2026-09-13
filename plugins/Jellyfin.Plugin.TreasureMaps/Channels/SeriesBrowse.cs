using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TreasureMaps.Api;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// One episode (or season pack) of a show, with every release/quality behind it.
/// </summary>
public sealed class EpisodeBundle
{
    /// <summary>Gets the parsed slot.</summary>
    public required EpisodeSlot Slot { get; init; }

    /// <summary>Gets the releases for this slot.</summary>
    public required List<Release> Releases { get; init; }

    /// <summary>Gets the newest posted date among the releases.</summary>
    public DateTimeOffset? Posted { get; init; }
}

/// <summary>
/// Season/episode parsed from a scene name.
/// </summary>
public readonly struct EpisodeSlot : IEquatable<EpisodeSlot>
{
    /// <summary>Unknown / unparseable releases dumped into one bucket.</summary>
    public static EpisodeSlot Other { get; } = new(null, null, null, true, false);

    /// <summary>Initializes a new instance of the <see cref="EpisodeSlot"/> struct.</summary>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The first episode number.</param>
    /// <param name="episodeEnd">The last episode of a multi-episode release.</param>
    /// <param name="unknown">Whether the name could not be parsed.</param>
    /// <param name="seasonPack">Whether this is a whole-season pack.</param>
    public EpisodeSlot(int? season, int? episode, int? episodeEnd, bool unknown, bool seasonPack)
    {
        Season = season;
        Episode = episode;
        EpisodeEnd = episodeEnd;
        IsUnknown = unknown;
        IsSeasonPack = seasonPack;
    }

    /// <summary>Gets the season number.</summary>
    public int? Season { get; }

    /// <summary>Gets the episode number.</summary>
    public int? Episode { get; }

    /// <summary>Gets the last episode of a multi-part release.</summary>
    public int? EpisodeEnd { get; }

    /// <summary>Gets a value indicating whether the scene name had no SxxExx.</summary>
    public bool IsUnknown { get; }

    /// <summary>Gets a value indicating whether this is a complete-season pack.</summary>
    public bool IsSeasonPack { get; }

    /// <summary>Gets the stable grouping key (<c>s01e04</c>, <c>s01pack</c>, <c>other</c>).</summary>
    public string Key
    {
        get
        {
            if (IsUnknown)
            {
                return "other";
            }

            var season = Season ?? 0;
            if (IsSeasonPack || !Episode.HasValue)
            {
                return "s" + season.ToString("00", CultureInfo.InvariantCulture) + "pack";
            }

            var start = "s" + season.ToString("00", CultureInfo.InvariantCulture)
                        + "e" + Episode.Value.ToString("00", CultureInfo.InvariantCulture);
            if (EpisodeEnd is > 0 && EpisodeEnd != Episode)
            {
                return start + "-e" + EpisodeEnd.Value.ToString("00", CultureInfo.InvariantCulture);
            }

            return start;
        }
    }

    /// <summary>Gets the Fire-TV / details-page label.</summary>
    public string Label
    {
        get
        {
            if (IsUnknown)
            {
                return "Other releases";
            }

            if (IsSeasonPack || !Episode.HasValue)
            {
                return "Season " + (Season ?? 0).ToString(CultureInfo.InvariantCulture) + " · Complete";
            }

            var label = "S" + (Season ?? 0).ToString("00", CultureInfo.InvariantCulture)
                        + "E" + Episode.Value.ToString("00", CultureInfo.InvariantCulture);
            if (EpisodeEnd is > 0 && EpisodeEnd != Episode)
            {
                label += "–E" + EpisodeEnd.Value.ToString("00", CultureInfo.InvariantCulture);
            }

            return label;
        }
    }

    /// <summary>Gets a name-sort key so S01E02 sits before S01E10.</summary>
    public string SortKey
    {
        get
        {
            if (IsUnknown)
            {
                return "z-other";
            }

            var season = (Season ?? 0).ToString("00", CultureInfo.InvariantCulture);
            if (IsSeasonPack || !Episode.HasValue)
            {
                return "s" + season + "e00";
            }

            return "s" + season + "e" + Episode.Value.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public bool Equals(EpisodeSlot other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EpisodeSlot other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
}

/// <summary>
/// How a series title card should list its children.
/// </summary>
public enum SeriesCoverLayout
{
    /// <summary>One episode (or none parsed) — list qualities directly.</summary>
    Qualities = 0,

    /// <summary>Several episodes in one season — list episode cards.</summary>
    Episodes = 1,

    /// <summary>More than one season — list season folders first.</summary>
    Seasons = 2
}

/// <summary>
/// Groups a show's indexer rows into seasons and episodes so opening a series cover
/// lists every Folge, not a flat quality dump of whichever page came back first.
/// </summary>
public static class SeriesBrowse
{
    private static readonly Regex Episode = new(
        @"[._\s\-][sS](?<s>\d{1,2})[eE](?<e>\d{1,3})(?:\s*[-–]\s*[eE]?(?<e2>\d{1,3})|[eE](?<e2>\d{1,3}))?",
        RegexOptions.Compiled);

    private static readonly Regex NumberX = new(
        @"[._\s\-](?<s>\d{1,2})x(?<e>\d{1,3})(?:[._\s]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeasonPack = new(
        @"[._\s\-](?:[sS](?<s>\d{1,2})(?![eE0-9])|(?:staffel|season)[._\s]?(?<s>\d{1,2})(?!\s*[eE]\d))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingYear = new(
        @"^(.+?)\s+((?:19|20)\d{2})$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses season/episode from a scene or API title.
    /// </summary>
    /// <param name="scene">The release title.</param>
    /// <returns>The slot (unknown when nothing matched).</returns>
    public static EpisodeSlot Parse(string? scene)
    {
        if (string.IsNullOrWhiteSpace(scene))
        {
            return EpisodeSlot.Other;
        }

        var padded = " " + scene;
        var episode = Episode.Match(padded);
        if (episode.Success)
        {
            var season = int.Parse(episode.Groups["s"].Value, CultureInfo.InvariantCulture);
            var start = int.Parse(episode.Groups["e"].Value, CultureInfo.InvariantCulture);
            int? end = null;
            if (episode.Groups["e2"].Success
                && int.TryParse(episode.Groups["e2"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e2)
                && e2 != start)
            {
                end = e2;
            }

            return new EpisodeSlot(season, start, end, false, false);
        }

        var numbered = NumberX.Match(padded);
        if (numbered.Success)
        {
            return new EpisodeSlot(
                int.Parse(numbered.Groups["s"].Value, CultureInfo.InvariantCulture),
                int.Parse(numbered.Groups["e"].Value, CultureInfo.InvariantCulture),
                null,
                false,
                false);
        }

        var pack = SeasonPack.Match(padded);
        if (pack.Success)
        {
            return new EpisodeSlot(
                int.Parse(pack.Groups["s"].Value, CultureInfo.InvariantCulture),
                null,
                null,
                false,
                true);
        }

        return EpisodeSlot.Other;
    }

    /// <summary>
    /// Folds releases into episode/season-pack bundles, newest posted kept on the bundle.
    /// </summary>
    /// <param name="releases">The show's releases.</param>
    /// <returns>Bundles ordered by season then episode.</returns>
    public static IReadOnlyList<EpisodeBundle> GroupEpisodes(IEnumerable<Release> releases)
    {
        var byKey = new Dictionary<string, List<Release>>(StringComparer.Ordinal);
        var slots = new Dictionary<string, EpisodeSlot>(StringComparer.Ordinal);

        foreach (var release in releases ?? Array.Empty<Release>())
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Guid))
            {
                continue;
            }

            var slot = Parse(release.Title);
            if (!byKey.TryGetValue(slot.Key, out var list))
            {
                list = [];
                byKey[slot.Key] = list;
                slots[slot.Key] = slot;
            }

            list.Add(release);
        }

        return byKey
            .Select(pair =>
            {
                var posted = pair.Value
                    .Select(r => r.PostedAt)
                    .Where(d => d.HasValue)
                    .Select(d => d!.Value)
                    .DefaultIfEmpty()
                    .Max();
                return new EpisodeBundle
                {
                    Slot = slots[pair.Key],
                    Releases = pair.Value,
                    Posted = posted == default ? null : posted
                };
            })
            .OrderBy(b => b.Slot.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Picks the child layout for a series cover.
    /// </summary>
    /// <param name="bundles">The episode bundles.</param>
    /// <returns>The layout.</returns>
    public static SeriesCoverLayout LayoutFor(IReadOnlyList<EpisodeBundle> bundles)
    {
        if (bundles is null || bundles.Count == 0)
        {
            return SeriesCoverLayout.Qualities;
        }

        var keys = bundles.Select(b => b.Slot.Key).Distinct(StringComparer.Ordinal).Count();
        if (keys < 2)
        {
            return SeriesCoverLayout.Qualities;
        }

        var seasons = bundles
            .Where(b => !b.Slot.IsUnknown && b.Slot.Season.HasValue)
            .Select(b => b.Slot.Season!.Value)
            .Distinct()
            .Count();

        return seasons > 1 ? SeriesCoverLayout.Seasons : SeriesCoverLayout.Episodes;
    }

    /// <summary>
    /// Distinct season numbers present in the bundles (unknown omitted).
    /// </summary>
    /// <param name="bundles">The bundles.</param>
    /// <returns>Sorted season numbers.</returns>
    public static IReadOnlyList<int> SeasonsOf(IEnumerable<EpisodeBundle> bundles)
        => bundles
            .Where(b => !b.Slot.IsUnknown && b.Slot.Season.HasValue)
            .Select(b => b.Slot.Season!.Value)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

    /// <summary>
    /// Bundles that belong to a season, plus unknown rows when <paramref name="season"/> is null.
    /// </summary>
    /// <param name="bundles">All bundles of the show.</param>
    /// <param name="season">The season number, or null for <c>other</c>.</param>
    /// <returns>The matching bundles.</returns>
    public static IReadOnlyList<EpisodeBundle> ForSeason(IEnumerable<EpisodeBundle> bundles, int? season)
    {
        if (season is null)
        {
            return bundles.Where(b => b.Slot.IsUnknown).ToList();
        }

        return bundles.Where(b => !b.Slot.IsUnknown && b.Slot.Season == season).ToList();
    }

    /// <summary>
    /// Releases of one episode key.
    /// </summary>
    /// <param name="bundles">All bundles of the show.</param>
    /// <param name="episodeKey">The slot key.</param>
    /// <returns>The releases, or empty.</returns>
    public static IReadOnlyList<Release> ReleasesFor(IEnumerable<EpisodeBundle> bundles, string episodeKey)
        => bundles
            .FirstOrDefault(b => string.Equals(b.Slot.Key, episodeKey, StringComparison.Ordinal))
            ?.Releases
            ?? [];

    /// <summary>
    /// Search strings that find more episodes than a year-suffixed card title alone.
    /// </summary>
    /// <param name="title">The show title on the card.</param>
    /// <returns>One or two queries, longest first.</returns>
    public static IReadOnlyList<string> SearchQueries(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var trimmed = title.Trim();
        var queries = new List<string> { trimmed };
        var year = TrailingYear.Match(trimmed);
        if (year.Success)
        {
            queries.Add(year.Groups[1].Value.Trim());
        }

        return queries.Where(q => q.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Season-targeted queries so early seasons are not buried under hundreds of new NZBs.
    /// </summary>
    /// <param name="title">The show title (year stripped when present).</param>
    /// <param name="seasons">Seasons already seen, plus the gap range to try.</param>
    /// <returns>Query strings such as <c>Silo S01</c>.</returns>
    public static IReadOnlyList<string> SeasonQueries(string title, IReadOnlyList<int> seasons)
    {
        var shortTitle = SearchQueries(title).LastOrDefault() ?? title;
        if (string.IsNullOrWhiteSpace(shortTitle))
        {
            return [];
        }

        var set = new SortedSet<int>(seasons ?? Array.Empty<int>());
        if (set.Count > 0)
        {
            var last = Math.Min(40, set.Max + 1);
            for (var i = 1; i <= last; i++)
            {
                set.Add(i);
            }
        }

        var queries = new List<string>();
        foreach (var season in set)
        {
            if (season < 1 || season > 40)
            {
                continue;
            }

            var padded = season.ToString("00", CultureInfo.InvariantCulture);
            queries.Add(shortTitle + " S" + padded);
            queries.Add(shortTitle + " S" + season.ToString(CultureInfo.InvariantCulture));
        }

        return queries;
    }

    /// <summary>
    /// True when a release belongs to the opened show (same grouping key or same title).
    /// </summary>
    /// <param name="release">The release.</param>
    /// <param name="key">The title-card grouping key.</param>
    /// <param name="title">The title-card display title.</param>
    /// <returns><c>true</c> when it is the same show.</returns>
    public static bool SameShow(Release release, string key, string title)
    {
        if (release is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(key)
            && string.Equals(ReleaseGrouper.KeyOf(release), key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var kind = ReleaseGrouper.KindOf(release);
        var resolved = ReleaseGrouper.TitleOf(release, kind);
        return ReleaseGrouper.SameTitle(resolved, title)
               || ReleaseGrouper.SameTitle(ReleaseGrouper.ShowNameFromScene(release.Title), title);
    }

    /// <summary>
    /// Label for a season folder on the series cover.
    /// </summary>
    /// <param name="season">The season number.</param>
    /// <param name="episodeCount">How many episode cards sit behind it.</param>
    /// <returns>The folder name.</returns>
    public static string SeasonLabel(int season, int episodeCount)
    {
        var name = "Season " + season.ToString(CultureInfo.InvariantCulture);
        if (episodeCount > 0)
        {
            name += " · " + episodeCount.ToString(CultureInfo.InvariantCulture)
                    + (episodeCount == 1 ? " episode" : " episodes");
        }

        return name;
    }

    /// <summary>
    /// Overview line for an episode card.
    /// </summary>
    /// <param name="releaseCount">How many qualities exist.</param>
    /// <returns>The overview.</returns>
    public static string EpisodeOverview(int releaseCount)
        => releaseCount == 1
            ? "1 release available. Open it and mark as favorite (\u2764) to download."
            : releaseCount.ToString(CultureInfo.InvariantCulture)
              + " releases available. Open the episode and mark a quality as favorite (\u2764) to download.";
}
