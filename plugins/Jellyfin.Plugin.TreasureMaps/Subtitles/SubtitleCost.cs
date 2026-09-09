using System;
using System.Globalization;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// A pre-start cost quote for AI subtitle creation (Whisper transcription + optional translation).
/// </summary>
public sealed class SubtitleQuote
{
    /// <summary>Gets or sets the media path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the target language (ISO 639-1).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the title (for the list row).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the duration in minutes (rounded up).</summary>
    public int Minutes { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double Seconds { get; set; }

    /// <summary>Gets or sets how many Whisper chunks will be sent.</summary>
    public int Chunks { get; set; }

    /// <summary>Gets or sets the Whisper model name.</summary>
    public string WhisperModel { get; set; } = "whisper-1";

    /// <summary>Gets or sets the estimated Whisper cost in USD.</summary>
    public decimal WhisperUsd { get; set; }

    /// <summary>Gets or sets the estimated translation cost in USD.</summary>
    public decimal TranslationUsd { get; set; }

    /// <summary>Gets the total estimated cost in USD.</summary>
    public decimal TotalUsd => WhisperUsd + TranslationUsd;

    /// <summary>Gets a value indicating whether a translation pass is included.</summary>
    public bool IncludesTranslation { get; set; }

    /// <summary>Gets or sets a human line for Fire TV / the web UI.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a sidecar already exists (load is free).</summary>
    public bool AlreadyExists { get; set; }
}

/// <summary>
/// Transparent pricing for AI subtitles. Defaults match OpenAI Whisper ($0.006/min) and
/// a cheap chat model for translation. Override via plugin config.
/// </summary>
public static class SubtitleCost
{
    /// <summary>Audio minutes per Whisper upload (keeps each file under the 25 MB API cap).</summary>
    public const int ChunkMinutes = 15;

    /// <summary>
    /// Builds a quote from duration and the configured rates.
    /// </summary>
    /// <param name="seconds">The media duration in seconds.</param>
    /// <param name="language">The target language.</param>
    /// <param name="whisperUsdPerMinute">Whisper price per audio minute.</param>
    /// <param name="translationUsdPerMillionTokens">Chat translation price per 1M tokens (in+out).</param>
    /// <param name="whisperModel">The Whisper model name.</param>
    /// <param name="title">Optional title for the summary line.</param>
    /// <returns>The quote.</returns>
    public static SubtitleQuote Build(
        double seconds,
        string language,
        decimal whisperUsdPerMinute,
        decimal translationUsdPerMillionTokens,
        string whisperModel,
        string? title = null)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(Math.Max(0, seconds) / 60.0));
        var chunks = Math.Max(1, (int)Math.Ceiling(minutes / (double)ChunkMinutes));
        var lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        var translate = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

        var whisper = decimal.Round(minutes * whisperUsdPerMinute, 4, MidpointRounding.AwayFromZero);
        // ~150 spoken words/min → ~200 tokens/min in + 200 out when translating the SRT.
        var tokens = translate ? minutes * 400m : 0m;
        var translation = decimal.Round(tokens / 1_000_000m * translationUsdPerMillionTokens, 4, MidpointRounding.AwayFromZero);

        var quote = new SubtitleQuote
        {
            Language = lang,
            Title = title,
            Minutes = minutes,
            Seconds = seconds,
            Chunks = chunks,
            WhisperModel = string.IsNullOrWhiteSpace(whisperModel) ? "whisper-1" : whisperModel,
            WhisperUsd = whisper,
            TranslationUsd = translation,
            IncludesTranslation = translate
        };
        quote.Summary = FormatSummary(quote);
        return quote;
    }

    /// <summary>
    /// Formats a one-line cost summary shown before the user starts generation.
    /// </summary>
    /// <param name="quote">The quote.</param>
    /// <returns>The summary.</returns>
    public static string FormatSummary(SubtitleQuote quote)
    {
        if (quote.AlreadyExists)
        {
            return "Bereits erzeugt · 0.00 USD · " + quote.Language.ToUpperInvariant()
                   + ".srt (tippen lädt, kein neuer API-Aufruf)";
        }

        var money = FormatUsd(quote.TotalUsd);
        var bits = "KI erzeugen · ca. " + money + " · " + quote.Minutes + " Min. Whisper";
        if (quote.IncludesTranslation)
        {
            bits += " + Übersetzung " + quote.Language.ToUpperInvariant();
        }

        bits += " (" + quote.Chunks + (quote.Chunks == 1 ? " Teil" : " Teile") + ")";
        return bits;
    }

    /// <summary>
    /// Formats a USD amount for UI (always shows cents).
    /// </summary>
    /// <param name="usd">The amount.</param>
    /// <returns>The formatted string.</returns>
    public static string FormatUsd(decimal usd)
        => usd.ToString("0.00", CultureInfo.InvariantCulture) + " USD";
}
