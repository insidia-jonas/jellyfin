using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Converts xAI word timestamps to readable, bounded subtitle cues.</summary>
public static class GrokTranscription
{
    /// <summary>Reads the documented /v1/stt response; never invents timestamps from plain text.</summary>
    public static (string Srt, string Language) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var language = root.TryGetProperty("language", out var lang) ? lang.GetString() ?? string.Empty : string.Empty;
        if (!root.TryGetProperty("words", out var words) || words.GetArrayLength() == 0)
        {
            if (root.TryGetProperty("text", out var transcript) && !string.IsNullOrWhiteSpace(transcript.GetString()))
            {
                throw new InvalidOperationException("Grok returned text without timestamps; a synchronized subtitle cannot be created.");
            }

            return (string.Empty, language);
        }

        var cues = new List<SrtCues.Cue>();
        var text = new StringBuilder();
        double start = 0, end = 0, lastStart = -1;
        foreach (var word in words.EnumerateArray())
        {
            var value = word.GetProperty("text").GetString()?.Trim();
            var from = word.GetProperty("start").GetDouble();
            var to = word.GetProperty("end").GetDouble();
            if (!double.IsFinite(from) || !double.IsFinite(to) || from < 0 || to < from || from < lastStart)
            {
                throw new InvalidOperationException("Grok returned invalid word timestamps.");
            }

            lastStart = from;
            if (string.IsNullOrEmpty(value)) { continue; }
            if (text.Length > 0 && (from - end > 0.8 || to - start > 6 || text.Length + value.Length > 80)) { Flush(); }
            if (text.Length == 0) { start = from; }
            else { text.Append(' '); }
            text.Append(value);
            end = Math.Max(to, end);
            if (text.Length >= 30 && ".!?".Contains(value[^1], StringComparison.Ordinal)) { Flush(); }
        }

        Flush();
        return (SrtCues.Render(cues), language);

        void Flush()
        {
            if (text.Length == 0) { return; }
            cues.Add(new SrtCues.Cue(cues.Count + 1, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(Math.Max(end, start + 0.1)), text.ToString()));
            text.Clear();
        }
    }
}
