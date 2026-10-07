using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Jellyfin.Extensions;

namespace Jellyfin.Plugin.TreasureMaps.Search;

/// <summary>
/// A typed search query with an optional year token ("matrix 1999").
/// </summary>
/// <param name="Text">The title text without the year.</param>
/// <param name="Year">The year, when the user typed one.</param>
public readonly record struct ParsedSearchQuery(string Text, int? Year)
{
    /// <summary>Gets the requested movie/TV scope, when explicit.</summary>
    public string? Kind { get; init; }
    /// <summary>Gets the season constraint.</summary>
    public int? Season { get; init; }
    /// <summary>Gets the episode constraint.</summary>
    public int? Episode { get; init; }
    /// <summary>Gets the normalized IMDb identity.</summary>
    public string? ImdbId { get; init; }
    /// <summary>Gets whether quotation marks require an exact title.</summary>
    public bool Exact { get; init; }
    /// <summary>Gets a canonical title without an informal part number before its subtitle.</summary>
    public string? AlternateText { get; init; }
}

/// <summary>
/// Scoring and eligibility for live Treasure-Maps search (native Search/Hints typeahead).
/// </summary>
public static class TreasureMapsSearch
{
    /// <summary>Minimum typed characters before the indexer is queried.</summary>
    public const int MinQueryLength = 2;

    /// <summary>Score aligned just below a library exact match (SQL uses 100).</summary>
    public const float ExactMatchScore = 95f;

    /// <summary>Score for a title that starts with the query (SQL prefix is 80).</summary>
    public const float PrefixMatchScore = 75f;

    /// <summary>Score for a word that starts with the query (SQL word-prefix is 75).</summary>
    public const float WordPrefixMatchScore = 70f;

    /// <summary>Score for a title that contains the query (SQL contains is 50).</summary>
    public const float ContainsMatchScore = 55f;

    /// <summary>Score when every query token appears in the title.</summary>
    public const float TokenMatchScore = 62f;

    /// <summary>
    /// Returns whether this is a global title search that should hit the indexer live.
    /// </summary>
    /// <param name="query">The Jellyfin search query.</param>
    /// <returns>True when the native search dialog should query Treasure-Maps.</returns>
    public static bool IsLiveTitleQuery(SearchProviderQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ParentId.HasValue && query.ParentId.Value != Guid.Empty)
        {
            return false;
        }

        var term = query.SearchTerm?.Trim() ?? string.Empty;
        if (term.Length < MinQueryLength)
        {
            return false;
        }

        if (query.MediaTypes.Length > 0 && query.MediaTypes.All(m => m == MediaType.Audio))
        {
            return false;
        }

        if (query.IncludeItemTypes.Length == 0)
        {
            return true;
        }

        return query.IncludeItemTypes.Any(t =>
            t is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.BoxSet);
    }

    /// <summary>
    /// Splits a typed query into title text and an optional year ("The Bear 2022").
    /// </summary>
    /// <param name="searchTerm">The raw query.</param>
    /// <returns>The parsed query.</returns>
    public static ParsedSearchQuery ParseQuery(string? searchTerm)
    {
        var parsed = MediaSearch.Parse(searchTerm);
        return new ParsedSearchQuery(parsed.Text, parsed.Year)
        {
            Kind = parsed.Kind, Season = parsed.Season, Episode = parsed.Episode,
            ImdbId = parsed.ImdbId, Exact = parsed.Exact, AlternateText = parsed.AlternateText
        };
    }

    /// <summary>Applies only documented indexer filters; credentials remain in the client.</summary>
    public static void ApplyParameters(Dictionary<string, string?> parameters, string? query, string kind)
    {
        var parsed = ParseQuery(query);
        parameters["q"] = string.IsNullOrWhiteSpace(parsed.Text) ? "*" : parsed.AlternateText ?? parsed.Text;
        parameters["year"] = parsed.Year?.ToString(CultureInfo.InvariantCulture);
        parameters["imdbid"] = parsed.ImdbId?[2..];
        if (kind == "tv")
        {
            parameters["season"] = parsed.Season?.ToString(CultureInfo.InvariantCulture);
            parameters["ep"] = parsed.Episode?.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Scores a specific identity, respecting explicit type, year and IMDb constraints.</summary>
    public static float ScoreIdentity(string? title, string query, int? year, string? kind, string? imdb)
    {
        var parsed = ParseQuery(query);
        if (parsed.Kind is not null && kind != parsed.Kind) { return 0; }
        if (parsed.Year.HasValue && year != parsed.Year) { return 0; }
        if (parsed.ImdbId is not null)
        {
            return ReleaseMapper.NormalizeImdbId(imdb ?? string.Empty) == parsed.ImdbId ? ExactMatchScore : 0;
        }

        return ScoreTitle(title, query, year);
    }

    /// <summary>Finds the best matching metadata or release-name alias in the whole group.</summary>
    public static string DisplayTitle(ReleaseGroup group, string query)
    {
        return group.Releases.SelectMany(r => new[] { ReleaseGrouper.TitleOf(r, group.Kind),
                group.Kind == "tv" ? ReleaseGrouper.ShowNameFromScene(r.Title) : ReleaseGrouper.CleanSceneTitle(r.Title) })
            .Prepend(group.Title).Where(t => !string.IsNullOrWhiteSpace(t))
            .OrderByDescending(t => ScoreTitle(t, ParseQuery(query).Text)).FirstOrDefault() ?? group.Title;
    }

    /// <summary>Rejects unrelated release identities and episodes before building title cards.</summary>
    public static float ScoreRelease(Release release, string query)
    {
        var parsed = ParseQuery(query);
        var group = ReleaseGrouper.Group(new[] { release }).FirstOrDefault();
        if (group is null) { return 0; }
        if (parsed.Season is int season)
        {
            var marker = Match(release.Title, @"(?:^|[._\s])S(\d{1,2})(?:E(\d{1,3}))?(?=[._\s-]|$)");
            if (!marker.Success || int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture) != season
                || (parsed.Episode is int ep && (!marker.Groups[2].Success || int.Parse(marker.Groups[2].Value, CultureInfo.InvariantCulture) != ep)))
            {
                return 0;
            }
        }

        return ScoreGroup(group, query);
    }

    /// <summary>Scores title aliases and the actual metadata without losing full-text matches during grouping.</summary>
    public static float ScoreGroup(ReleaseGroup group, string query)
        => ScoreDocument(DisplayTitle(group, query), query, group.Year, group.Kind, group.Imdb,
            string.Join("\n", group.Releases.SelectMany(r => new[] { ReleaseGrouper.TitleOf(r, group.Kind),
                group.Kind == "tv" ? ReleaseGrouper.ShowNameFromScene(r.Title) : ReleaseGrouper.CleanSceneTitle(r.Title) }).Distinct().Take(12)),
            group.Plot, string.Join(' ', group.Genres.Concat(group.Actors).Append(group.Director ?? string.Empty)));

    /// <summary>Scores an identity and its searchable document consistently after materialization.</summary>
    public static float ScoreDocument(string? title, string query, int? year, string? kind, string? imdb, string? aliases, string? overview, string? credits)
    {
        var parsed = MediaSearch.Parse(query);
        if ((parsed.Kind is not null && kind != parsed.Kind) || (parsed.Year.HasValue && year != parsed.Year)) { return 0; }
        if (parsed.ImdbId is not null) { return ReleaseMapper.NormalizeImdbId(imdb ?? string.Empty) == parsed.ImdbId ? ExactMatchScore : 0; }
        return MediaSearch.ScoreDocument(parsed, (aliases ?? string.Empty).Split('\n').Prepend(title), overview, credits);
    }

    /// <summary>When the requested title exists, omit loose word matches to other titles.</summary>
    public static List<Release> RelevantReleases(IEnumerable<Release> releases, string query)
    {
        var scored = releases.Select(r => (Release: r, Score: ScoreRelease(r, query))).Where(r => r.Score > 0).ToList();
        // Retain franchise subtitles (Insidious: Out of the Further) even when the original
        // film is an exact match. Suppress unrelated middle-word matches such as Masha and the Bear.
        var threshold = scored.Any(r => r.Score >= ExactMatchScore - 2) ? PrefixMatchScore : 1;
        return scored.Where(r => r.Score >= threshold).OrderByDescending(r => r.Score).Select(r => r.Release).ToList();
    }

    private static Match Match(string text, string pattern)
        => Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Scores a title against a typed query for Search/Hints ranking.
    /// </summary>
    /// <param name="title">The release / card title.</param>
    /// <param name="searchTerm">The typed query.</param>
    /// <returns>A relevance score (higher is better).</returns>
    public static float ScoreTitle(string? title, string searchTerm)
        => ScoreTitle(title, searchTerm, titleYear: null);

    /// <summary>
    /// Scores a title against a typed query, optionally boosting a matching year.
    /// </summary>
    /// <param name="title">The release / card title.</param>
    /// <param name="searchTerm">The typed query.</param>
    /// <param name="titleYear">The title's production year, may be null.</param>
    /// <returns>A relevance score (higher is better).</returns>
    public static float ScoreTitle(string? title, string searchTerm, int? titleYear)
    {
        var parsed = ParseQuery(searchTerm);
        if (parsed.Year.HasValue && titleYear != parsed.Year) { return 0; }
        var score = MediaSearch.ScoreTitle(title, MediaSearch.Parse(searchTerm));
        if (parsed.Exact && score < ExactMatchScore - 2f) { return 0; }
        return parsed.Year.HasValue && score > 0 ? Math.Min(99f, score + 4f) : score;
    }

    /// <summary>
    /// Picks the title that scores best against the typed query (metadata vs scene name).
    /// The indexer often stores "The Bear" as "The Bear King of the Kitchen".
    /// </summary>
    /// <param name="metaTitle">The API metadata title.</param>
    /// <param name="sceneTitle">The title parsed from the scene/release name.</param>
    /// <param name="searchTerm">The typed query.</param>
    /// <returns>The better display title.</returns>
    public static string BestDisplayTitle(string? metaTitle, string? sceneTitle, string searchTerm)
    {
        var meta = (metaTitle ?? string.Empty).Trim();
        var scene = (sceneTitle ?? string.Empty).Trim();
        var metaScore = ScoreTitle(meta, ParseQuery(searchTerm).Text);
        var sceneScore = ScoreTitle(scene, ParseQuery(searchTerm).Text);
        if (sceneScore > metaScore && scene.Length > 0)
        {
            return scene;
        }

        return meta.Length > 0 ? meta : scene;
    }

}
