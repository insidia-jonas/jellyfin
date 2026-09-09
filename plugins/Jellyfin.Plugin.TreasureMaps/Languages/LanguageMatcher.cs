using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TreasureMaps.Languages;

/// <summary>
/// Preferences describing which release languages to accept and how to rank them.
/// </summary>
/// <param name="Primary">The primary (most preferred) language.</param>
/// <param name="Secondary">Accepted secondary languages, in order of preference.</param>
/// <param name="FilterOut">Whether releases matching none of the languages are dropped.</param>
public readonly record struct LanguagePreferences(string? Primary, IReadOnlyList<string>? Secondary, bool FilterOut)
{
    /// <summary>Gets the accepted secondary languages (never null).</summary>
    public IReadOnlyList<string> SecondaryOrEmpty => Secondary ?? Array.Empty<string>();

    /// <summary>Gets a value indicating whether any language preference is configured.</summary>
    public bool HasAny => !string.IsNullOrWhiteSpace(Primary) || SecondaryOrEmpty.Any(s => !string.IsNullOrWhiteSpace(s));
}

/// <summary>
/// The outcome of matching a release's languages against the configured preferences.
/// </summary>
/// <param name="Keep">Whether the release should be shown.</param>
/// <param name="Rank">Sort rank (lower is better): 0 primary, 1+ secondary, large for unknown.</param>
/// <param name="Label">A short label for the matched language, or null.</param>
public readonly record struct LanguageMatch(bool Keep, int Rank, string? Label);

/// <summary>
/// Normalizes and ranks languages so a primary/secondary preference can be applied to releases.
/// </summary>
public static class LanguageMatcher
{
    private const int UnknownRank = 10_000;

    // Maps common ISO 639-1/2 codes and English/native names to a canonical ISO 639-1 code.
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["german"] = "de", ["deutsch"] = "de",
        ["en"] = "en", ["eng"] = "en", ["english"] = "en",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es", ["espanol"] = "es", ["español"] = "es",
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr", ["francais"] = "fr", ["français"] = "fr",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it", ["italiano"] = "it",
        ["nl"] = "nl", ["dut"] = "nl", ["nld"] = "nl", ["dutch"] = "nl", ["nederlands"] = "nl",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt", ["português"] = "pt",
        ["ru"] = "ru", ["rus"] = "ru", ["russian"] = "ru",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
        ["zh"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["chinese"] = "zh",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko",
        ["pl"] = "pl", ["pol"] = "pl", ["polish"] = "pl",
        ["sv"] = "sv", ["swe"] = "sv", ["swedish"] = "sv",
        ["da"] = "da", ["dan"] = "da", ["danish"] = "da",
        ["no"] = "no", ["nor"] = "no", ["norwegian"] = "no",
        ["fi"] = "fi", ["fin"] = "fi", ["finnish"] = "fi"
    };

    /// <summary>
    /// Normalizes a language token to a canonical code (best effort).
    /// </summary>
    /// <param name="value">The raw language string.</param>
    /// <returns>The canonical code, or the trimmed lowercase input when unknown.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var token = value.Trim();
        if (_aliases.TryGetValue(token, out var canonical))
        {
            return canonical;
        }

        // Handle values like "German (Germany)" or "en-US".
        var head = token.Split(' ', '(', '-', '_')[0];
        return _aliases.TryGetValue(head, out var canonical2) ? canonical2 : head.ToLowerInvariant();
    }

    /// <summary>
    /// Matches release languages against the preferences.
    /// </summary>
    /// <param name="prefs">The configured preferences.</param>
    /// <param name="releaseLanguages">The release's languages (e.g. audio languages).</param>
    /// <returns>The match result.</returns>
    public static LanguageMatch Match(LanguagePreferences prefs, IEnumerable<string>? releaseLanguages)
    {
        if (!prefs.HasAny)
        {
            return new LanguageMatch(true, UnknownRank, null);
        }

        var normalized = (releaseLanguages ?? Enumerable.Empty<string>())
            .Select(Normalize)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        // No language info: keep it, but rank last so matched releases sort first.
        if (normalized.Count == 0)
        {
            return new LanguageMatch(true, UnknownRank, null);
        }

        var primary = Normalize(prefs.Primary);
        if (!string.IsNullOrEmpty(primary) && normalized.Contains(primary))
        {
            return new LanguageMatch(true, 0, primary.ToUpperInvariant());
        }

        var secondaries = prefs.SecondaryOrEmpty;
        for (var i = 0; i < secondaries.Count; i++)
        {
            var secondary = Normalize(secondaries[i]);
            if (!string.IsNullOrEmpty(secondary) && normalized.Contains(secondary))
            {
                return new LanguageMatch(true, i + 1, secondary.ToUpperInvariant());
            }
        }

        // Has language info but matches nothing: drop only when filtering is enabled.
        return new LanguageMatch(!prefs.FilterOut, UnknownRank + 1, null);
    }
}
