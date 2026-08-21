using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TreasureMaps.Api;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// A group of releases that all belong to the same movie or TV show. The Treasure-Maps website
/// shows one poster per title and lists the individual releases behind it; this mirrors that so a
/// title appears once (with a cover) instead of once per release.
/// </summary>
public sealed class ReleaseGroup
{
    /// <summary>Gets or sets the stable grouping key (tmdb/imdb/normalized title).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the display title (movie/show name).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind (<c>movie</c> or <c>tv</c>).</summary>
    public string Kind { get; set; } = "movie";

    /// <summary>Gets or sets the cover/poster URL, if any release in the group has one.</summary>
    public string? Cover { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the plot/description.</summary>
    public string? Plot { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? Imdb { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public string? Tmdb { get; set; }

    /// <summary>Gets the actors.</summary>
    public List<string> Actors { get; } = new();

    /// <summary>Gets the genres.</summary>
    public List<string> Genres { get; } = new();

    /// <summary>Gets the releases in this group.</summary>
    public List<Release> Releases { get; } = new();
}

/// <summary>
/// Pure logic that folds a flat list of releases into per-title groups (kept separate from the
/// channel so it can be unit tested).
/// </summary>
public static class ReleaseGrouper
{
    private static readonly Regex EpisodeMarker = new(@"[._\s][sS]\d{1,2}[eE]\d{1,3}", RegexOptions.Compiled);
    private static readonly Regex SeasonMarker = new(@"[._\s](?:[sS]\d{1,2}|staffel|season)[._\s]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex YearMarker = new(@"[._\s(](19|20)\d{2}([._\s)]|$)", RegexOptions.Compiled);

    /// <summary>
    /// Groups releases by the movie/show they belong to, preserving first-seen order.
    /// </summary>
    /// <param name="releases">The releases to group.</param>
    /// <returns>The ordered list of groups.</returns>
    public static IReadOnlyList<ReleaseGroup> Group(IEnumerable<Release> releases)
    {
        var groups = new List<ReleaseGroup>();
        var byKey = new Dictionary<string, ReleaseGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var release in releases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.Guid))
            {
                continue;
            }

            var kind = KindOf(release);
            var title = TitleOf(release, kind);
            var key = KeyOf(release, kind, title);

            if (!byKey.TryGetValue(key, out var group))
            {
                group = new ReleaseGroup { Key = key, Title = title, Kind = kind };
                byKey[key] = group;
                groups.Add(group);
            }

            group.Releases.Add(release);

            // Fill in poster/metadata from whichever release in the group carries it.
            group.Cover ??= release.Images?.Cover;
            if (!group.Year.HasValue)
            {
                group.Year = kind == "tv"
                    ? ParseYear(FirstFour(release.Tv?.FirstAired))
                    : ParseYear(release.Movie?.Year);
            }

            if (!group.Rating.HasValue)
            {
                group.Rating = ParseRating(release.Movie?.Rating);
            }

            if (group.Genres.Count == 0 && release.Movie?.Genres is { Count: > 0 } genres)
            {
                group.Genres.AddRange(genres);
            }

            group.Plot ??= release.Movie?.Plot ?? release.Tv?.Overview;
            group.Tagline ??= release.Movie?.Tagline;
            group.Imdb ??= release.Ids?.Imdb ?? release.Tv?.Imdb;
            group.Tmdb ??= release.Ids?.Tmdb ?? release.Tv?.Tmdb;
            if (group.Actors.Count == 0 && release.Movie?.Actors is { Count: > 0 } actors)
            {
                group.Actors.AddRange(actors);
            }
        }

        return groups;
    }

    /// <summary>
    /// Determines whether a release is a movie or TV release.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns><c>movie</c> or <c>tv</c>.</returns>
    public static string KindOf(Release release)
    {
        if (release.Movie is not null)
        {
            return "movie";
        }

        if (release.Tv is not null)
        {
            return "tv";
        }

        var cat = release.Category?.Name;
        if (!string.IsNullOrEmpty(cat) && cat.Contains("TV", StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        if (!string.IsNullOrEmpty(release.Title) && EpisodeMarker.IsMatch(release.Title))
        {
            return "tv";
        }

        return "movie";
    }

    /// <summary>
    /// Resolves the display title (movie/show name) for a release.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <param name="kind">The release kind.</param>
    /// <returns>The display title.</returns>
    public static string TitleOf(Release release, string kind)
    {
        var meta = kind == "tv" ? release.Tv?.Title : release.Movie?.Title;
        if (!string.IsNullOrWhiteSpace(meta))
        {
            return meta!.Trim();
        }

        return kind == "tv" ? ShowNameFromScene(release.Title) : CleanSceneTitle(release.Title);
    }

    /// <summary>
    /// Builds the stable grouping key for a release (resolving kind and title automatically).
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns>The grouping key.</returns>
    public static string KeyOf(Release release)
    {
        var kind = KindOf(release);
        return KeyOf(release, kind, TitleOf(release, kind));
    }

    /// <summary>
    /// Builds a stable grouping key: external id when available, else normalized title + year.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <param name="kind">The release kind.</param>
    /// <param name="title">The resolved title.</param>
    /// <returns>The grouping key.</returns>
    public static string KeyOf(Release release, string kind, string title)
    {
        var tmdb = release.Ids?.Tmdb ?? release.Tv?.Tmdb;
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            return kind + ":tmdb:" + tmdb;
        }

        var imdb = release.Ids?.Imdb ?? release.Tv?.Imdb;
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            return kind + ":imdb:" + imdb;
        }

        var year = kind == "tv" ? FirstFour(release.Tv?.FirstAired) : release.Movie?.Year;
        return kind + ":t:" + Normalize(title) + (string.IsNullOrWhiteSpace(year) ? string.Empty : ":" + year);
    }

    /// <summary>
    /// Derives a show name from a scene release name (strips the episode/season/year and everything after).
    /// </summary>
    /// <param name="scene">The scene release name.</param>
    /// <returns>The derived show name.</returns>
    public static string ShowNameFromScene(string? scene)
    {
        if (string.IsNullOrWhiteSpace(scene))
        {
            return string.Empty;
        }

        var cut = scene;
        var m = EpisodeMarker.Match(cut);
        if (m.Success)
        {
            cut = cut[..m.Index];
        }
        else
        {
            var s = SeasonMarker.Match(cut);
            if (s.Success)
            {
                cut = cut[..s.Index];
            }
        }

        return Prettify(cut);
    }

    /// <summary>
    /// Cleans a movie scene name into a human title (strips the year and technical suffix).
    /// </summary>
    /// <param name="scene">The scene release name.</param>
    /// <returns>The cleaned title.</returns>
    public static string CleanSceneTitle(string? scene)
    {
        if (string.IsNullOrWhiteSpace(scene))
        {
            return string.Empty;
        }

        var cut = scene;
        var y = YearMarker.Match(cut);
        if (y.Success && y.Index > 0)
        {
            cut = cut[..y.Index];
        }

        return Prettify(cut);
    }

    private static string Prettify(string value)
        => value.Replace('.', ' ').Replace('_', ' ').Trim();

    private static string Normalize(string title)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string? FirstFour(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length < 4 ? value : value[..4];

    private static int? ParseYear(string? year)
        => int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseRating(string? rating)
        => double.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
