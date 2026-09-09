using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TreasureMaps.ReleaseNaming;

/// <summary>
/// Attributes parsed from a scene/P2P release name.
/// </summary>
public sealed class ParsedRelease
{
    /// <summary>Gets or sets the resolution label (e.g. <c>2160p</c>, <c>1080p</c>).</summary>
    public string? Resolution { get; set; }

    /// <summary>Gets or sets the source label (e.g. <c>BluRay</c>, <c>WEB-DL</c>, <c>CAM</c>, <c>TELESYNC</c>).</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the audio-source quality for theatrical rips:
    /// <c>MIC</c> (microphone – worse), <c>LINE</c> (line/direct audio – better) or <c>MD</c> (mic dubbed).
    /// </summary>
    public string? AudioSource { get; set; }

    /// <summary>Gets or sets the video codec (e.g. <c>AVC</c>/<c>H.264</c>, <c>HEVC</c>/<c>H.265</c>, <c>XviD</c>).</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the HDR label (e.g. <c>HDR</c>, <c>DV</c>).</summary>
    public string? Hdr { get; set; }

    /// <summary>Gets or sets a value indicating whether the release is dual-language (<c>DL</c>).</summary>
    public bool DualLanguage { get; set; }

    /// <summary>Gets the languages detected in the name (English names, e.g. <c>German</c>).</summary>
    public List<string> Languages { get; } = new();

    /// <summary>Gets or sets the release group.</summary>
    public string? Group { get; set; }

    /// <summary>
    /// Gets a coarse quality score (higher is better) derived from source and audio-source, used for
    /// ranking. Not authoritative — Jellyfin ultimately sorts channel folders by name.
    /// </summary>
    public int QualityScore { get; set; }

    /// <summary>Gets an ordered list of short display tags for the parsed attributes.</summary>
    public IReadOnlyList<string> DisplayTags
    {
        get
        {
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(Resolution))
            {
                tags.Add(Resolution!);
            }

            if (!string.IsNullOrEmpty(Source))
            {
                tags.Add(Source!);
            }

            if (!string.IsNullOrEmpty(AudioSource))
            {
                tags.Add(AudioSource!);
            }

            if (!string.IsNullOrEmpty(Codec))
            {
                tags.Add(Codec!);
            }

            if (!string.IsNullOrEmpty(Hdr))
            {
                tags.Add(Hdr!);
            }

            if (DualLanguage)
            {
                tags.Add("DL");
            }

            if (!string.IsNullOrEmpty(Group))
            {
                tags.Add("[" + Group + "]");
            }

            return tags;
        }
    }
}

/// <summary>
/// Parses scene/P2P release names (e.g.
/// <c>Pinocchio Unstrung 2026 GERMAN DL 1080P BLURAY AVC-UNDERTAKERS</c>) into structured attributes.
/// </summary>
public static class ReleaseNameParser
{
    private static readonly Regex _groupRegex = new(@"-([A-Za-z0-9]+)\s*$", RegexOptions.Compiled);
    private static readonly char[] _separators = { '.', ' ', '_', '-', '[', ']', '(', ')' };

    /// <summary>
    /// Parses a release name into its scene attributes.
    /// </summary>
    /// <param name="name">The release/scene name.</param>
    /// <returns>The parsed attributes (never null).</returns>
    public static ParsedRelease Parse(string? name)
    {
        var result = new ParsedRelease();
        if (string.IsNullOrWhiteSpace(name))
        {
            return result;
        }

        var groupMatch = _groupRegex.Match(name);
        var body = name;
        if (groupMatch.Success)
        {
            result.Group = groupMatch.Groups[1].Value.ToUpperInvariant();
            body = name[..groupMatch.Index];
        }

        var tokens = body
            .Split(_separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToUpperInvariant())
            .ToList();
        var set = new HashSet<string>(tokens, StringComparer.Ordinal);

        result.Resolution = MatchResolution(set);
        result.Source = MatchSource(set);
        result.AudioSource = MatchAudioSource(set);
        result.Codec = MatchCodec(set);
        result.Hdr = MatchHdr(set);
        DetectLanguages(set, result);
        result.QualityScore = ComputeQuality(result);
        return result;
    }

    private static string? MatchResolution(HashSet<string> t)
    {
        if (t.Contains("2160P") || t.Contains("2160") || t.Contains("4K") || t.Contains("UHD"))
        {
            return "2160p";
        }

        if (t.Contains("1080P") || t.Contains("1080") || t.Contains("1080I"))
        {
            return "1080p";
        }

        if (t.Contains("720P") || t.Contains("720"))
        {
            return "720p";
        }

        if (t.Contains("576P"))
        {
            return "576p";
        }

        return t.Contains("480P") ? "480p" : null;
    }

    private static string? MatchSource(HashSet<string> t)
    {
        if (t.Overlaps(new[] { "REMUX" }))
        {
            return "Remux";
        }

        if (t.Overlaps(new[] { "BLURAY", "BLU-RAY", "BDRIP", "BRRIP", "BDR", "BD25", "BD50", "BDMV" }))
        {
            return "BluRay";
        }

        if (t.Overlaps(new[] { "WEB-DL", "WEBDL", "WEB.DL" }) || t.Contains("WEB"))
        {
            return "WEB-DL";
        }

        if (t.Contains("WEBRIP"))
        {
            return "WEBRip";
        }

        if (t.Contains("HDTV"))
        {
            return "HDTV";
        }

        if (t.Contains("HDRIP"))
        {
            return "HDRip";
        }

        if (t.Overlaps(new[] { "DVDRIP", "DVD", "DVD5", "DVD9" }))
        {
            return "DVDRip";
        }

        if (t.Overlaps(new[] { "DVDSCR", "BDSCR", "SCR", "SCREENER" }))
        {
            return "Screener";
        }

        if (t.Overlaps(new[] { "HDCAM", "CAMRIP", "CAM" }))
        {
            return "CAM";
        }

        if (t.Overlaps(new[] { "HDTS", "TELESYNC", "PDVD", "TS" }))
        {
            return "TELESYNC";
        }

        if (t.Overlaps(new[] { "HDTC", "TELECINE", "TC" }))
        {
            return "TELECINE";
        }

        if (t.Contains("R5"))
        {
            return "R5";
        }

        return t.Overlaps(new[] { "WORKPRINT", "WP" }) ? "Workprint" : null;
    }

    private static string? MatchAudioSource(HashSet<string> t)
    {
        // Only meaningful for theatrical rips, but the tokens are unambiguous enough to surface anywhere.
        if (t.Overlaps(new[] { "LINE", "LD" }))
        {
            return "LINE"; // line/direct audio – better
        }

        if (t.Contains("MD"))
        {
            return "MD"; // mic dubbed
        }

        return t.Contains("MIC") ? "MIC" : null; // microphone – worse
    }

    private static string? MatchCodec(HashSet<string> t)
    {
        if (t.Overlaps(new[] { "HEVC", "H265", "H.265", "X265" }))
        {
            return "HEVC";
        }

        if (t.Overlaps(new[] { "AVC", "H264", "H.264", "X264" }))
        {
            return "AVC";
        }

        if (t.Contains("AV1"))
        {
            return "AV1";
        }

        if (t.Contains("XVID"))
        {
            return "XviD";
        }

        return t.Contains("DIVX") ? "DivX" : null;
    }

    private static string? MatchHdr(HashSet<string> t)
    {
        if (t.Overlaps(new[] { "DV", "DOVI", "DOLBYVISION" }))
        {
            return "DV";
        }

        return t.Overlaps(new[] { "HDR", "HDR10", "HDR10+" }) ? "HDR" : null;
    }

    private static void DetectLanguages(HashSet<string> t, ParsedRelease result)
    {
        if (t.Contains("DL") || t.Contains("DUAL"))
        {
            result.DualLanguage = true;
        }

        var map = new (string Token, string Name)[]
        {
            ("GERMAN", "German"), ("DEUTSCH", "German"),
            ("ENGLISH", "English"),
            ("FRENCH", "French"), ("VF", "French"),
            ("ITALIAN", "Italian"),
            ("SPANISH", "Spanish"),
            ("DUTCH", "Dutch"),
            ("RUSSIAN", "Russian"),
            ("JAPANESE", "Japanese"),
            ("KOREAN", "Korean"),
            ("MULTI", "Multi")
        };

        foreach (var (token, langName) in map)
        {
            if (t.Contains(token) && !result.Languages.Contains(langName, StringComparer.OrdinalIgnoreCase))
            {
                result.Languages.Add(langName);
            }
        }
    }

    private static int ComputeQuality(ParsedRelease r)
    {
        var score = r.Source switch
        {
            "Remux" => 100,
            "BluRay" => 90,
            "WEB-DL" => 80,
            "WEBRip" => 70,
            "HDTV" => 60,
            "HDRip" => 55,
            "DVDRip" => 50,
            "Screener" => 40,
            "R5" => 35,
            "TELECINE" => 30,
            "TELESYNC" => 20,
            "CAM" => 10,
            "Workprint" => 5,
            _ => 0
        };

        // For theatrical rips, line audio is clearly better than microphone audio.
        score += r.AudioSource switch
        {
            "LINE" => 5,
            "MD" => 2,
            "MIC" => -3,
            _ => 0
        };

        return score;
    }
}
