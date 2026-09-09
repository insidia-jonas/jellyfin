using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TreasureMaps.Channels;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// SAB job names were often the quality badge ("1080p · WEB-DL · …") instead of the movie title.
/// Resolve a human title for Downloads cards and the details page.
/// </summary>
public static class DownloadTitle
{
    private static readonly Regex QualityOnly = new(
        @"^(?:\d{3,4}p|4k|uhd|sd)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasQualityDots = new(
        @"\d{3,4}p\s*[·•]\s*(WEB|BLU|HDTV|CAM|TS|SCR|REMUX|ENCODE)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool LooksLikeQualityLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 4)
        {
            return true;
        }

        if (QualityOnly.IsMatch(trimmed))
        {
            return true;
        }

        if (HasQualityDots.IsMatch(trimmed) && !trimmed.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(trimmed, "Start download", StringComparison.OrdinalIgnoreCase)
               || string.Equals(trimmed, "Download starten", StringComparison.OrdinalIgnoreCase);
    }

    public static string Resolve(string? sabJobName, string? storedTitle)
    {
        if (!string.IsNullOrWhiteSpace(storedTitle) && !LooksLikeQualityLabel(storedTitle))
        {
            return storedTitle.Trim();
        }

        if (string.IsNullOrWhiteSpace(sabJobName))
        {
            return "Download";
        }

        var raw = sabJobName.Trim();
        if (LooksLikeQualityLabel(raw))
        {
            return "Download";
        }

        var cleaned = LooksLikeTvScene(raw)
            ? ReleaseGrouper.ShowNameFromScene(raw)
            : ReleaseGrouper.CleanSceneTitle(raw);
        if (string.IsNullOrWhiteSpace(cleaned) || LooksLikeQualityLabel(cleaned))
        {
            return "Download";
        }

        return cleaned;
    }

    private static bool LooksLikeTvScene(string name)
        => Regex.IsMatch(name, @"[._\s][sS]\d{1,2}[eE]\d{1,3}|[._\s](?:staffel|season)[._\s]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
