using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Extensions;

/// <summary>A user's title query and explicitly requested media constraints.</summary>
/// <param name="Text">Title or descriptive words.</param>
public sealed record MediaSearchQuery(string Text)
{
    /// <summary>Gets the requested movie or TV scope.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the season number.</summary>
    public int? Season { get; init; }

    /// <summary>Gets the episode number.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets the IMDb identity.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets a value indicating whether the title was quoted.</summary>
    public bool Exact { get; init; }

    /// <summary>Gets a title alias when an informal installment number precedes a specific subtitle.</summary>
    public string? AlternateText { get; init; }
}

/// <summary>Shared, deterministic title and full-text matching for library and indexer search.</summary>
public static class MediaSearch
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "der", "die", "das", "ein", "eine", "und", "and", "of", "von", "zu", "im", "in"
    };

    /// <summary>Parses explicit filters without interpreting title numbers as episode numbers.</summary>
    /// <param name="input">User input.</param>
    /// <returns>The parsed query.</returns>
    public static MediaSearchQuery Parse(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        text = text[..Math.Min(text.Length, 256)];
        string? kind = null;
        int? year = null, season = null, episode = null;
        var scope = Match(text, @"^(film|movie|serie|series|tv):\s*");
        if (scope.Success)
        {
            kind = scope.Groups[1].Value.ToLowerInvariant() is "film" or "movie" ? "movie" : "tv";
            text = text[scope.Length..];
        }

        // Accept either filter order, including natural German season/episode notation.
        for (var pass = 0; pass < 2; pass++)
        {
            var marker = Match(text, @"(?:^|[\s.])(?:S(\d{1,2})(?:E(\d{1,3}))?|(\d{1,2})x(\d{1,3})|(?:staffel|season)\s*(\d{1,2})(?:\s*(?:folge|episode)\s*(\d{1,3}))?)\s*$");
            if (marker.Success)
            {
                var s = marker.Groups[1].Success ? 1 : marker.Groups[3].Success ? 3 : 5;
                season = int.Parse(marker.Groups[s].Value, CultureInfo.InvariantCulture);
                episode = marker.Groups[s + 1].Success ? int.Parse(marker.Groups[s + 1].Value, CultureInfo.InvariantCulture) : null;
                kind = "tv";
                text = text[..marker.Index].Trim();
            }

            var date = Match(text, @"\s+(?:\((19\d{2}|20\d{2})\)|(19\d{2}|20\d{2}))$");
            if (date.Success)
            {
                var value = int.Parse(date.Groups[1].Success ? date.Groups[1].Value : date.Groups[2].Value, CultureInfo.InvariantCulture);
                if (value <= DateTime.UtcNow.Year + 2)
                {
                    year = value;
                    text = text[..date.Index].Trim();
                }
            }
        }

        var exact = text.Length > 1 && text[0] == '"' && text[^1] == '"';
        if (exact)
        {
            text = text[1..^1].Trim();
        }

        var imdb = Match(text, @"^(?:imdb:)?(tt\d{7,10})$");
        var numberedSubtitle = Match(text, @"^(.+?)\s+\d{1,2}\s*[-–—:]\s*(.+)$");
        var alternate = !exact && numberedSubtitle.Success && Keywords(numberedSubtitle.Groups[2].Value).Length >= 2
            ? numberedSubtitle.Groups[1].Value.Trim() + " " + numberedSubtitle.Groups[2].Value.Trim()
            : null;
        return new MediaSearchQuery(imdb.Success ? string.Empty : text)
        {
            Kind = kind,
            Year = year,
            Season = season,
            Episode = episode,
            Exact = exact,
            ImdbId = imdb.Success ? imdb.Groups[1].Value.ToLowerInvariant() : null,
            AlternateText = alternate
        };
    }

    /// <summary>Normalizes case, accents and punctuation for matching.</summary>
    /// <param name="value">Text.</param>
    /// <returns>Space-separated normalized words.</returns>
    public static string Normalize(string? value)
    {
        var text = value ?? string.Empty;
        text = text[..Math.Min(text.Length, 8192)];
        var output = new StringBuilder(text.Length);
        foreach (var c in text.Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                output.Append(c);
            }
            else if (output.Length > 0 && output[^1] != ' ')
            {
                output.Append(' ');
            }
        }

        return output.ToString().Trim();
    }

    /// <summary>Gets useful bounded words for candidate retrieval.</summary>
    /// <param name="text">Query text.</param>
    /// <returns>Words, retaining short titles when they consist only of common words.</returns>
    public static string[] Keywords(string text)
    {
        var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12).ToArray();
        var useful = words.Where(w => !StopWords.Contains(w)).ToArray();
        return useful.Length > 0 ? useful : words;
    }

    /// <summary>Scores title matches; exact titles outrank prefixes, reordered words and typos.</summary>
    /// <param name="title">Candidate title.</param>
    /// <param name="query">Parsed query.</param>
    /// <returns>A score from zero to 95.</returns>
    public static float ScoreTitle(string? title, MediaSearchQuery query)
    {
        if (query.AlternateText is not null)
        {
            return Math.Max(ScoreTitle(title, query with { AlternateText = null }),
                Math.Max(0, ScoreTitle(title, query with { Text = query.AlternateText, AlternateText = null }) - 1));
        }

        var name = Normalize(title);
        var term = Normalize(query.Text);
        if (name.Length == 0 || term.Length == 0)
        {
            return 0;
        }

        if (name == term)
        {
            return 95;
        }

        var n = StripArticle(name);
        var q = StripArticle(term);
        if (n.Length == 0 || q.Length == 0)
        {
            return 0;
        }

        if (n == q)
        {
            return 93;
        }

        if (query.Exact)
        {
            return 0;
        }

        if (n.StartsWith(q, StringComparison.Ordinal))
        {
            return 75;
        }

        if ((" " + n).Contains(" " + q, StringComparison.Ordinal))
        {
            return 70;
        }

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12).ToArray();
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (MatchWords(tokens, words, fuzzy: false))
        {
            return 62;
        }

        return MatchWords(tokens, words, fuzzy: true) ? 45 : 0;
    }

    /// <summary>Matches all descriptive words across titles and metadata, below title matches.</summary>
    /// <param name="query">Parsed query.</param>
    /// <param name="titles">Known title aliases.</param>
    /// <param name="metadata">Overview, genres or credited names.</param>
    /// <returns>Relevance, with metadata-only matches scored below fuzzy title matches.</returns>
    public static float ScoreDocument(MediaSearchQuery query, IEnumerable<string?> titles, params string?[] metadata)
    {
        var aliases = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var score = aliases.Select(t => ScoreTitle(t, query)).DefaultIfEmpty(0).Max();
        if (score > 0 || query.Exact || query.ImdbId is not null)
        {
            return score;
        }

        var tokens = Keywords(query.Text);
        if (tokens.Length == 0 || tokens.All(t => t.Length < 3))
        {
            return 0;
        }

        var words = Normalize(string.Join(' ', aliases.Concat(metadata))).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return MatchWords(tokens, words, fuzzy: false) ? 30 : 0;
    }

    /// <summary>Builds at most two constrained fallbacks after a provider returns no useful title.</summary>
    /// <param name="query">Original query.</param>
    /// <returns>Relaxed title queries; constraints are retained.</returns>
    public static IReadOnlyList<string> FallbackQueries(MediaSearchQuery query)
    {
        if (query.Exact || query.ImdbId is not null)
        {
            return [];
        }

        var words = Keywords(query.Text);
        var variants = new List<string>();
        if (words.Length > 1)
        {
            variants.Add(string.Join(' ', words[..^1]));
            variants.Add(string.Join(' ', words[1..]));
        }
        else if (words.Length == 1 && words[0].Length >= 5)
        {
            variants.Add(words[0][..Math.Min(4, words[0].Length - 2)]);
        }

        return variants.Where(v => v.Length >= 3).Distinct(StringComparer.Ordinal)
            .Select(v => (query.Kind is null ? string.Empty : query.Kind + ": ") + v
                + (query.Year.HasValue ? " (" + query.Year.Value.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty)
                + (query.Season.HasValue ? " S" + query.Season.Value.ToString("00", CultureInfo.InvariantCulture) : string.Empty)
                + (query.Episode.HasValue ? "E" + query.Episode.Value.ToString("00", CultureInfo.InvariantCulture) : string.Empty))
            .ToArray();
    }

    private static bool MatchWords(string[] tokens, string[] words, bool fuzzy)
    {
        var available = words.ToList();
        var errors = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var index = available.FindIndex(w => w == token);
            if (index < 0 && i == tokens.Length - 1 && token.Length >= 2)
            {
                index = available.FindIndex(w => w.StartsWith(token, StringComparison.Ordinal));
            }

            if (index < 0 && fuzzy && token.Length >= 4 && errors < Math.Max(1, tokens.Length / 3))
            {
                index = available.FindIndex(w => NearWord(w, token));
                errors++;
            }

            if (index < 0)
            {
                return false;
            }

            available.RemoveAt(index);
        }

        return tokens.Length > 0;
    }

    private static bool NearWord(string word, string token)
    {
        var max = token.Length >= 8 ? 2 : 1;
        if (word.Length < 4 || word.Length > 64 || token.Length > 64 || Math.Abs(word.Length - token.Length) > max
            || word.Any(char.IsDigit) || token.Any(char.IsDigit))
        {
            return false;
        }

        var distance = new int[word.Length + 1, token.Length + 1];
        for (var i = 0; i <= word.Length; i++)
        {
            distance[i, 0] = i;
        }

        for (var j = 0; j <= token.Length; j++)
        {
            distance[0, j] = j;
        }

        for (var i = 1; i <= word.Length; i++)
        {
            for (var j = 1; j <= token.Length; j++)
            {
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + (word[i - 1] == token[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && word[i - 1] == token[j - 2] && word[i - 2] == token[j - 1])
                {
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
                }
            }
        }

        return distance[word.Length, token.Length] <= max;
    }

    private static string StripArticle(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 && StopWords.Contains(text[..space]) ? text[(space + 1)..] : text;
    }

    private static Match Match(string text, string pattern)
        => Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
