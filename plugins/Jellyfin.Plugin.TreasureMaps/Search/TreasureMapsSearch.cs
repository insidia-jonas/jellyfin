using System;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TreasureMaps.Search;

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
    /// Scores a title against a typed query for Search/Hints ranking.
    /// </summary>
    /// <param name="title">The release / card title.</param>
    /// <param name="searchTerm">The typed query.</param>
    /// <returns>A relevance score (higher is better).</returns>
    public static float ScoreTitle(string? title, string searchTerm)
    {
        var name = (title ?? string.Empty).Trim();
        var term = (searchTerm ?? string.Empty).Trim();
        if (name.Length == 0 || term.Length == 0)
        {
            return 0f;
        }

        if (name.Equals(term, StringComparison.OrdinalIgnoreCase))
        {
            return ExactMatchScore;
        }

        // "bear" should rank "The Bear" just under an exact match (web/TV users type without the article).
        var strippedName = StripLeadingArticle(name);
        var strippedTerm = StripLeadingArticle(term);
        if (strippedName.Length > 0
            && (strippedName.Equals(term, StringComparison.OrdinalIgnoreCase)
                || strippedName.Equals(strippedTerm, StringComparison.OrdinalIgnoreCase)))
        {
            return ExactMatchScore - 2f;
        }

        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)
            || strippedName.StartsWith(term, StringComparison.OrdinalIgnoreCase)
            || strippedName.StartsWith(strippedTerm, StringComparison.OrdinalIgnoreCase))
        {
            return PrefixMatchScore;
        }

        var padded = " " + name + " ";
        if (padded.Contains(" " + term, StringComparison.OrdinalIgnoreCase))
        {
            return WordPrefixMatchScore;
        }

        if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return ContainsMatchScore;
        }

        return 0f;
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
        var metaScore = ScoreTitle(meta, searchTerm);
        var sceneScore = ScoreTitle(scene, searchTerm);
        if (sceneScore > metaScore && scene.Length > 0)
        {
            return scene;
        }

        return meta.Length > 0 ? meta : scene;
    }

    private static string StripLeadingArticle(string value)
    {
        foreach (var article in new[] { "The ", "A ", "An ", "Der ", "Die ", "Das ", "Le ", "La ", "El ", "Los ", "Las " })
        {
            if (value.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return value[article.Length..].TrimStart();
            }
        }

        return value;
    }
}
