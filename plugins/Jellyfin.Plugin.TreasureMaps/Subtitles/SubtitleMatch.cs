using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TreasureMaps.Languages;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Fail closed on conflicting identities. A popular or fuzzy title hit is not a film match.</summary>
public static class SubtitleMatch
{
    public static long? Id(IReadOnlyDictionary<string, string> ids, string key)
        => ids.TryGetValue(key, out var value) && long.TryParse(value.Trim().TrimStart('t', 'T'), out var id) && id > 0 ? id : null;

    public static bool Accept(SubtitleSearchRequest request, OsSubtitleAttributes candidate, string language, bool hashPass)
    {
        if (LanguageMatcher.Normalize(candidate.Language) != LanguageMatcher.Normalize(language)) return false;
        var exactFile = hashPass && candidate.MoviehashMatch;
        if (hashPass && !exactFile) return false;
        var feature = candidate.Feature;
        if (feature is null) return exactFile;
        var episode = request.ContentType == VideoContentType.Episode;
        if (!string.IsNullOrWhiteSpace(feature.Type)
            && !feature.Type.Equals(episode ? "Episode" : "Movie", StringComparison.OrdinalIgnoreCase)) return false;
        var imdb = Id(request.ProviderIds, "Imdb");
        var tmdb = Id(request.ProviderIds, "Tmdb");
        if (Conflict(imdb, feature.Imdb) || Conflict(tmdb, feature.Tmdb)) return false;
        var exactIdentity = Same(imdb, feature.Imdb) || Same(tmdb, feature.Tmdb);
        if (episode)
        {
            var parentImdb = Id(request.SeriesProviderIds, "Imdb");
            var parentTmdb = Id(request.SeriesProviderIds, "Tmdb");
            if (Conflict(parentImdb, feature.ParentImdb) || Conflict(parentTmdb, feature.ParentTmdb)) return false;
            if (request.ParentIndexNumber.HasValue && feature.Season.HasValue && request.ParentIndexNumber != feature.Season) return false;
            if (request.IndexNumber.HasValue && feature.Episode.HasValue && request.IndexNumber != feature.Episode) return false;
            if (exactIdentity || exactFile) return true;
            if (!request.ParentIndexNumber.HasValue || !request.IndexNumber.HasValue
                || request.ParentIndexNumber != feature.Season || request.IndexNumber != feature.Episode) return false;
            return Same(parentImdb, feature.ParentImdb) || Same(parentTmdb, feature.ParentTmdb)
                || TitleMatches(feature.ParentTitle, request.SeriesName, request.OriginalTitle);
        }

        if (exactIdentity || exactFile) return true;
        if (request.ProductionYear.HasValue && request.ProductionYear != feature.Year) return false;
        return TitleMatches(feature.Title, request.Name, request.OriginalTitle);
    }

    private static bool Same(long? expected, long? actual) => expected is > 0 && expected == actual;
    private static bool Conflict(long? expected, long? actual) => expected is > 0 && actual is > 0 && expected != actual;

    private static bool TitleMatches(string? candidate, params string?[] names)
    {
        var title = Normalize(candidate);
        return title.Length > 0 && names.Any(name => title == Normalize(name));
    }

    private static string Normalize(string? title)
    {
        var result = new StringBuilder();
        foreach (var c in (title ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }
}
