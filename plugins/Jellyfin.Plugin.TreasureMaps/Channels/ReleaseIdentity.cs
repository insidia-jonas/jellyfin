using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TreasureMaps.Api;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>Rejects contradictory provider metadata before a release can represent a title.</summary>
public static class ReleaseIdentity
{
    private static readonly Regex Year = new(@"(?<![\p{L}\d])(?<year>(?:19|20)\d{2})(?![\p{L}\d])", RegexOptions.Compiled);
    private static readonly Regex Technical = new(@"[ ._\-](?:2160p|1080[pi]|720p|480p|BluRay|BDRip|BRRip|WEB[ ._\-]?DL|WEBRip|HDTV|DVDRip|REMUX|x26[45]|h[ .]?26[45])(?:[ ._\-]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Reads the release year before technical suffixes; title numbers remain intact.</summary>
    public static int? SceneYear(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var technical = Technical.Match(name);
        var text = technical.Success ? name[..technical.Index] : name;
        var years = Year.Matches(text);
        if (years.Count == 0) return null;
        var last = years[^1];
        // A leading number may be the entire title, such as "1917.1080p...".
        if (last.Index == 0) return null;
        return int.TryParse(last.Groups["year"].Value, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    /// <summary>A release file from 2012 cannot be the movie identified by metadata from 2026.</summary>
    public static bool HasConsistentMetadata(Release release)
    {
        if (release is null) return false;
        // TV release dates describe episodes/seasons, not necessarily the series premiere.
        if (ReleaseGrouper.KindOf(release) == "tv") return true;
        var sceneYear = SceneYear(release.Title);
        return !sceneYear.HasValue || !int.TryParse(release.Movie?.Year, out var metadataYear)
            || Math.Abs(sceneYear.Value - metadataYear) <= 1;
    }

    /// <summary>Normalize numeric IMDb identifiers before comparing identities.</summary>
    public static string NormalizeId(string kind, string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (kind == "imdb" && text.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }
}
