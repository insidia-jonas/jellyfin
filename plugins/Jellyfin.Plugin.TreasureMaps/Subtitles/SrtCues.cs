using System;
using System.Collections.Generic;
using System.Globalization;
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
        var blocks = SplitBlocks(srt);
        var sb = new StringBuilder();
        var index = startIndex;
        foreach (var block in blocks)
        {
            var shifted = Timestamp.Replace(block, m =>
            {
                var start = Read(m, 1) + offset;
                var end = Read(m, 5) + offset;
                return Format(start) + " --> " + Format(end);
            });

            var lines = shifted.Split('\n');
            if (lines.Length == 0)
            {
                continue;
            }

            lines[0] = index.ToString(CultureInfo.InvariantCulture);
            sb.Append(string.Join('\n', lines).Trim()).Append("\n\n");
            index++;
        }

        return (sb.ToString(), index);
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
