using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
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

        // Parse the scene/release name for attributes the structured API fields may not carry
        // (source, dual-language, MIC/LINE audio source, group, ...).
        var parsed = ReleaseNameParser.Parse(release.Title);

        // Language can come from the structured field and/or the name (e.g. "GERMAN DL").
        var releaseLanguages = (release.AudioLanguages ?? Enumerable.Empty<string>()).Concat(parsed.Languages);
        var languageMatch = LanguageMatcher.Match(languages, releaseLanguages);
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

        // Releases are modelled as *folders*, not playable media: they have no stream, and on TV
        // clients a playable item would show a "Play" button that errors. As a folder the item has
        // no Play button; opening it shows a small grab detail, and the download is triggered by
        // marking it as a favorite (handled by GrabOnFavoriteService via the ProviderIds below).
        var item = new ChannelItemInfo
        {
            Id = release.Guid,
            Name = title!,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            Overview = BuildOverview(release),
            ImageUrl = release.Images?.Cover,
            HomePageUrl = release.Links?.Details,
            CommunityRating = rating.HasValue ? (float)rating.Value : null,
            ProductionYear = isTv ? ParseYear(FirstFour(tv?.FirstAired)) : ParseYear(movie?.Year),
            DateCreated = release.PostedAt?.UtcDateTime
        };

        if (movie?.Genres is { Count: > 0 })
        {
            item.Genres = movie.Genres.ToList();
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
        ApplyParsedName(item, parsed);

        // Tag with the release id + kind so favouriting the item in the normal UI can trigger a grab.
        item.ProviderIds["TreasureMaps"] = release.Guid;
        item.ProviderIds["TreasureMapsKind"] = isTv ? "tv" : "movie";

        var imdb = release.Ids?.Imdb ?? tv?.Imdb;
        if (!string.IsNullOrWhiteSpace(imdb))
        {
            item.ProviderIds["Imdb"] = NormalizeImdbId(imdb!);
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

    private static void ApplyParsedName(ChannelItemInfo item, ParsedRelease parsed)
    {
        foreach (var tag in parsed.DisplayTags)
        {
            if (!item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                item.Tags.Add(tag);
            }
        }

        // Spell out the theatrical audio source, since MIC vs LINE is a big quality signal.
        var audioNote = parsed.AudioSource switch
        {
            "MIC" => "Audio source: MIC (microphone recording \u2013 lower quality)",
            "LINE" => "Audio source: LINE (direct/line audio \u2013 higher quality)",
            "MD" => "Audio source: MD (mic dubbed)",
            _ => null
        };

        if (audioNote is not null)
        {
            item.Overview = string.IsNullOrEmpty(item.Overview) ? audioNote : item.Overview + "\n\n" + audioNote;
        }
    }

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
    /// Builds a short quality-badge label for a release (e.g. <c>1080p · BluRay · AVC · German · DL · 9.8 GB · [GROUP]</c>),
    /// used as the tile name inside a title card instead of the raw scene name. Falls back to the
    /// scene name when nothing could be parsed.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns>The badge label, never null.</returns>
    public static string BuildQualityLabel(Release release)
    {
        var parsed = ReleaseNameParser.Parse(release.Title);
        var badges = new List<string>();
        void Add(string? badge)
        {
            if (!string.IsNullOrEmpty(badge))
            {
                badges.Add(badge!);
            }
        }

        Add(parsed.Resolution);
        Add(parsed.Source);
        Add(parsed.AudioSource);
        Add(parsed.Codec);
        Add(parsed.Hdr);
        foreach (var language in parsed.Languages)
        {
            Add(language);
        }

        if (parsed.DualLanguage)
        {
            Add("DL");
        }

        if (release.Size > 0)
        {
            Add(FormatSize(release.Size));
        }

        if (!string.IsNullOrEmpty(parsed.Group))
        {
            Add("[" + parsed.Group + "]");
        }

        return badges.Count > 0 ? string.Join(" \u00b7 ", badges) : (release.Title ?? string.Empty);
    }

    /// <summary>
    /// Normalizes an IMDb id to the canonical <c>tt</c>-prefixed form (the indexer returns bare
    /// numeric ids, which would produce broken imdb.com links).
    /// </summary>
    /// <param name="id">The raw IMDb id.</param>
    /// <returns>The normalized id.</returns>
    public static string NormalizeImdbId(string id)
        => id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id : "tt" + id;

    /// <summary>
    /// Builds a short flag-emoji prefix for the audio languages of a release (from the API's
    /// audio_languages and/or the scene name), e.g. <c>🇩🇪</c> for a German release.
    /// Returns an empty string when no language could be detected.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns>The flag prefix (up to three flags), never null.</returns>
    public static string LanguageFlags(Release release)
    {
        var parsed = ReleaseNameParser.Parse(release.Title);
        var languages = (release.AudioLanguages ?? Enumerable.Empty<string>()).Concat(parsed.Languages);

        var flags = new List<string>();
        foreach (var language in languages)
        {
            var flag = FlagFor(language);
            if (flag is not null && !flags.Contains(flag))
            {
                flags.Add(flag);
            }
        }

        return string.Concat(flags.Take(3));
    }

    private static string? FlagFor(string language) => language.Trim().ToLowerInvariant() switch
    {
        "de" or "ger" or "deu" or "german" or "deutsch" => "\U0001F1E9\U0001F1EA",
        "en" or "eng" or "english" => "\U0001F1EC\U0001F1E7",
        "fr" or "fre" or "fra" or "french" => "\U0001F1EB\U0001F1F7",
        "es" or "spa" or "spanish" => "\U0001F1EA\U0001F1F8",
        "it" or "ita" or "italian" => "\U0001F1EE\U0001F1F9",
        "nl" or "dut" or "nld" or "dutch" => "\U0001F1F3\U0001F1F1",
        "ru" or "rus" or "russian" => "\U0001F1F7\U0001F1FA",
        "ja" or "jpn" or "japanese" => "\U0001F1EF\U0001F1F5",
        "ko" or "kor" or "korean" => "\U0001F1F0\U0001F1F7",
        "multi" or "mul" => "\U0001F310",
        _ => null
    };

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
