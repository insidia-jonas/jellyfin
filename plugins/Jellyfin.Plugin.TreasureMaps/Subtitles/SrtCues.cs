using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Parses, shifts and concatenates SRT cues so Whisper chunks become one file.
/// </summary>
public static class SrtCues
{
    private static readonly Regex Timestamp = new(
        @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s+-->\s+(\d{2}):(\d{2}):(\d{2})[,.](\d{3})",
        RegexOptions.Compiled);

    /// <summary>
    /// Shifts every timestamp in an SRT document by <paramref name="offset"/> and reindexes cues.
    /// </summary>
    /// <param name="srt">The SRT text.</param>
    /// <param name="offset">The time to add.</param>
    /// <param name="startIndex">The first cue number.</param>
    /// <returns>The shifted SRT and the next cue index.</returns>
    public static (string Text, int NextIndex) Shift(string srt, TimeSpan offset, int startIndex)
    {
        var cues = Parse(srt);
        return (Render(cues.Select((cue, i) => cue with { Index = startIndex + i, Start = cue.Start + offset, End = cue.End + offset })), startIndex + cues.Count);
    }

    /// <summary>A validated subtitle cue with timestamps independent of translated text.</summary>
    public sealed record Cue(int Index, TimeSpan Start, TimeSpan End, string Text);

    /// <summary>Reads subtitle cues, retaining indexes and rejecting invalid time intervals.</summary>
    public static IReadOnlyList<Cue> Parse(string srt)
    {
        var cues = new List<Cue>();
        foreach (var block in SplitBlocks(srt))
        {
            var match = Timestamp.Match(block);
            var start = Read(match, 1);
            var end = Read(match, 5);
            var text = block[(match.Index + match.Length)..].Trim();
            var prefix = block[..match.Index].Trim();
            var index = int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : cues.Count + 1;
            if (end <= start || string.IsNullOrWhiteSpace(text)) { throw new InvalidOperationException("Invalid subtitle cue returned by the speech service."); }
            cues.Add(new Cue(index, start, end, text));
        }

        return cues;
    }

    /// <summary>Renders valid subtitle cues without allowing the translator to change timing.</summary>
    public static string Render(IEnumerable<Cue> cues)
        => Concat(cues.Select(c => c.Index.ToString(CultureInfo.InvariantCulture) + "\n" + Format(c.Start) + " --> " + Format(c.End) + "\n" + c.Text));

    /// <summary>Applies a complete JSON translation, retaining every original index and timestamp.</summary>
    public static string ApplyTranslation(string source, string response)
    {
        var cues = Parse(source);
        var first = response.IndexOf('[', StringComparison.Ordinal);
        var last = response.LastIndexOf(']');
        if (first < 0 || last < first) { throw new InvalidOperationException("The translation service did not return subtitle text as JSON."); }
        using var doc = JsonDocument.Parse(response[first..(last + 1)]);
        var translated = new Dictionary<int, string>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var index = row.GetProperty("index").GetInt32();
            var text = row.GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(text) || !translated.TryAdd(index, text.Trim()))
            {
                throw new InvalidOperationException("The translation contains missing or duplicate subtitle cues.");
            }
        }

        if (translated.Count != cues.Count || cues.Any(c => !translated.ContainsKey(c.Index)))
        {
            throw new InvalidOperationException("The translation is incomplete. No incorrect language file was saved.");
        }

        return Render(cues.Select(c => c with { Text = Regex.Replace(translated[c.Index], @"\r?\n\s*\n", "\n") }));
    }

    /// <summary>
    /// Joins already-shifted SRT documents.
    /// </summary>
    /// <param name="parts">The parts.</param>
    /// <returns>One SRT file.</returns>
    public static string Concat(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                sb.Append(part.Trim()).Append("\n\n");
            }
        }

        return sb.ToString().Trim() + "\n";
    }

    /// <summary>
    /// Splits an SRT into chunks of at most <paramref name="maxChars"/> (whole cues).
    /// </summary>
    /// <param name="srt">The SRT text.</param>
    /// <param name="maxChars">The maximum characters per chunk.</param>
    /// <returns>The chunks.</returns>
    public static IReadOnlyList<string> Chunk(string srt, int maxChars)
    {
        var blocks = SplitBlocks(srt);
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (sb.Length + block.Length > maxChars && sb.Length > 0)
            {
                chunks.Add(sb.ToString().Trim());
                sb.Clear();
            }

            sb.Append(block.Trim()).Append("\n\n");
        }

        if (sb.Length > 0)
        {
            chunks.Add(sb.ToString().Trim());
        }

        return chunks;
    }

    private static List<string> SplitBlocks(string srt)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(srt))
        {
            return blocks;
        }

        var parts = Regex.Split(srt.Replace("\r\n", "\n", StringComparison.Ordinal), @"\n\s*\n");
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part) && Timestamp.IsMatch(part))
            {
                blocks.Add(part.Trim());
            }
        }

        return blocks;
    }

    private static TimeSpan Read(Match match, int group)
    {
        var h = int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups[group + 1].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(match.Groups[group + 2].Value, CultureInfo.InvariantCulture);
        var ms = int.Parse(match.Groups[group + 3].Value, CultureInfo.InvariantCulture);
        return new TimeSpan(0, h, m, s, ms);
    }

    private static string Format(TimeSpan value)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            (int)value.TotalHours,
            value.Minutes,
            value.Seconds,
            value.Milliseconds);
}
