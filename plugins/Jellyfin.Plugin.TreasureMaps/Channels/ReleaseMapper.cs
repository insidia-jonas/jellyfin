using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.Xrel;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Maps Treasure-Maps API releases to Jellyfin channel items. Pure, side-effect free logic
/// (kept separate from the channel so it can be unit tested).
/// </summary>
public static class ReleaseMapper
{
    /// <summary>
    /// Converts a release into a rich channel item, applying the minimum-rating filter.
    /// </summary>
    /// <param name="release">The release to map.</param>
    /// <param name="minRating">The minimum community rating (0 disables the filter).</param>
    /// <returns>The mapped channel item, or <c>null</c> if the release is invalid or filtered out.</returns>
    public static ChannelItemInfo? ToChannelItem(Release release, double minRating)
        => ToChannelItem(release, minRating, default, null, out _);

    /// <summary>
    /// Converts a release into a rich channel item, applying rating and language filters.
    /// </summary>
    /// <param name="release">The release to map.</param>
    /// <param name="minRating">The minimum community rating (0 disables the filter).</param>
    /// <param name="languages">The language preferences.</param>
    /// <param name="xrel">Optional xREL rating for this release.</param>
    /// <param name="languageRank">Outputs the language sort rank (lower is better).</param>
    /// <returns>The mapped channel item, or <c>null</c> if the release is invalid or filtered out.</returns>
    public static ChannelItemInfo? ToChannelItem(Release release, double minRating, LanguagePreferences languages, XrelRating? xrel, out int languageRank)
    {
        languageRank = 0;
        if (release is null || string.IsNullOrWhiteSpace(release.Guid))
        {
            return null;
        }

        var languageMatch = LanguageMatcher.Match(languages, release.AudioLanguages);
        if (!languageMatch.Keep)
        {
            return null;
        }

        languageRank = languageMatch.Rank;
        var movie = release.Movie;
        var tv = release.Tv;
        var isTv = movie is null && tv is not null;

        var rating = ParseRating(movie?.Rating);
        if (minRating > 0 && rating.HasValue && rating.Value < minRating)
        {
            return null;
        }

        var title = movie?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = tv?.Title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = release.Title;
        }

        var item = new ChannelItemInfo
        {
            Id = release.Guid,
            Name = title!,
            Type = ChannelItemType.Media,
            ContentType = isTv ? ChannelMediaContentType.Episode : ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            Overview = BuildOverview(release),
            ImageUrl = release.Images?.Cover,
            HomePageUrl = release.Links?.Details,
            CommunityRating = rating.HasValue ? (float)rating.Value : null,
            ProductionYear = isTv ? ParseYear(FirstFour(tv?.FirstAired)) : ParseYear(movie?.Year)
        };

        if (!string.IsNullOrWhiteSpace(movie?.Genres))
        {
            item.Genres = movie!.Genres!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        var quality = string.Join(' ', new[] { release.Video?.Resolution, release.Video?.Codec }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(quality))
        {
            item.Tags.Add(quality);
        }

        item.Tags.Add(FormatSize(release.Size));

        if (!string.IsNullOrEmpty(languageMatch.Label))
        {
            item.Tags.Add(languageMatch.Label);
        }

        ApplyXrel(item, xrel);

        var imdb = release.Ids?.Imdb ?? tv?.Imdb;
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            item.ProviderIds["Imdb"] = imdb!;
        }

        var tmdb = release.Ids?.Tmdb ?? tv?.Tmdb;
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            item.ProviderIds["Tmdb"] = tmdb!;
        }

        return item;
    }

    private static string? FirstFour(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length < 4 ? value : value[..4];

    private static void ApplyXrel(ChannelItemInfo item, XrelRating? xrel)
    {
        if (xrel is not { HasAny: true } rating)
        {
            return;
        }

        var parts = new List<string>();
        if (rating.VideoRating.HasValue)
        {
            parts.Add("V" + rating.VideoRating.Value.ToString("0.#", CultureInfo.InvariantCulture));
        }

        if (rating.AudioRating.HasValue)
        {
            parts.Add("A" + rating.AudioRating.Value.ToString("0.#", CultureInfo.InvariantCulture));
        }

        var label = "xREL";
        if (parts.Count > 0)
        {
            label += " " + string.Join("/", parts);
            if (rating.NumRatings > 0)
            {
                label += " (" + rating.NumRatings.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        item.Tags.Add(label);

        // Use the xREL title rating as the community rating when the indexer has none.
        if (!item.CommunityRating.HasValue && rating.TitleRating.HasValue)
        {
            item.CommunityRating = (float)rating.TitleRating.Value;
        }

        var overviewLine = "xREL rating: " + (parts.Count > 0 ? string.Join(", ", parts) : "n/a");
        if (rating.TitleRating.HasValue)
        {
            overviewLine += " | title " + rating.TitleRating.Value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        item.Overview = string.IsNullOrEmpty(item.Overview) ? overviewLine : item.Overview + "\n\n" + overviewLine;
    }

    /// <summary>
    /// Formats a byte count as a human-readable size.
    /// </summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>A human-readable size string.</returns>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "unknown size";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unit]);
    }

    private static string BuildOverview(Release release)
    {
        var movie = release.Movie;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(movie?.Tagline))
        {
            parts.Add(movie!.Tagline!);
        }

        if (!string.IsNullOrWhiteSpace(movie?.Plot))
        {
            parts.Add(movie!.Plot!);
        }

        var tech = new List<string>();
        if (!string.IsNullOrWhiteSpace(release.Video?.Resolution))
        {
            tech.Add(release.Video!.Resolution!);
        }

        if (!string.IsNullOrWhiteSpace(release.Video?.Codec))
        {
            tech.Add(release.Video!.Codec!);
        }

        tech.Add(FormatSize(release.Size));
        if (release.Grabs > 0)
        {
            tech.Add(release.Grabs.ToString(CultureInfo.InvariantCulture) + " grabs");
        }

        parts.Add("Release: " + release.Title);
        parts.Add(string.Join(" \u2022 ", tech));
        return string.Join("\n\n", parts);
    }

    private static double? ParseRating(string? rating)
        => double.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? ParseYear(string? year)
        => int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
