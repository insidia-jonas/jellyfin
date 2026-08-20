using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
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
    {
        if (release is null || string.IsNullOrWhiteSpace(release.Guid))
        {
            return null;
        }

        var movie = release.Movie;
        var rating = ParseRating(movie?.Rating);
        if (minRating > 0 && rating.HasValue && rating.Value < minRating)
        {
            return null;
        }

        var item = new ChannelItemInfo
        {
            Id = release.Guid,
            Name = string.IsNullOrWhiteSpace(movie?.Title) ? release.Title : movie!.Title!,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            Overview = BuildOverview(release),
            ImageUrl = release.Images?.Cover,
            HomePageUrl = release.Links?.Details,
            CommunityRating = rating.HasValue ? (float)rating.Value : null,
            ProductionYear = ParseYear(movie?.Year)
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

        if (!string.IsNullOrWhiteSpace(release.Ids?.Imdb))
        {
            item.ProviderIds["Imdb"] = release.Ids!.Imdb!;
        }

        if (!string.IsNullOrWhiteSpace(release.Ids?.Tmdb))
        {
            item.ProviderIds["Tmdb"] = release.Ids!.Tmdb!;
        }

        return item;
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
