using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Recommendations;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Quotes and creates subtitles: Whisper transcription of the file, then optional LLM translation
/// into the requested language. The quote is meant to be shown before generation starts.
/// </summary>
public sealed class AiSubtitleService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiRecommender _ai;
    private readonly ILogger<AiSubtitleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSubtitleService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="ai">The configured chat provider (translation).</param>
    /// <param name="logger">The logger.</param>
    public AiSubtitleService(IHttpClientFactory httpClientFactory, AiRecommender ai, ILogger<AiSubtitleService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ai = ai;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Whether AI subtitle creation is enabled and has a key.
    /// </summary>
    public static bool IsEnabled =>
        Config.EnableAiSubtitles
        && (!string.IsNullOrWhiteSpace(Config.WhisperApiKey) || !string.IsNullOrWhiteSpace(Config.AiApiKey));

    /// <summary>
    /// Builds a cost quote for a library file.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <param name="language">The target language.</param>
    /// <param name="title">The title, for the list row.</param>
    /// <param name="runtimeTicks">Optional runtime from Jellyfin metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The quote.</returns>
    public async Task<SubtitleQuote> QuoteAsync(
        string path,
        string language,
        string? title,
        long? runtimeTicks,
        CancellationToken cancellationToken)
    {
        var seconds = await MediaProbe.GetDurationSecondsAsync(path, cancellationToken).ConfigureAwait(false)
                      ?? (runtimeTicks is > 0 ? TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds : 0);
        if (seconds <= 0)
        {
            seconds = 60;
        }

        var quote = SubtitleCost.Build(
            seconds,
            language,
            Config.WhisperUsdPerMinute > 0 ? Config.WhisperUsdPerMinute : 0.006m,
            Config.TranslationUsdPerMillionTokens > 0 ? Config.TranslationUsdPerMillionTokens : 0.15m,
            string.IsNullOrWhiteSpace(Config.WhisperModel) ? "whisper-1" : Config.WhisperModel,
            title);
        quote.Path = path;
        quote.AlreadyExists = SubtitleFiles.TryRead(path, language, out _);
        if (quote.AlreadyExists)
        {
            quote.Summary = SubtitleCost.FormatSummary(quote);
        }

        return quote;
    }

    /// <summary>
    /// Transcribes the file (and translates when the target language is not English).
    /// </summary>
    /// <param name="quote">The quote the user accepted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SRT text.</returns>
    public async Task<string> GenerateAsync(SubtitleQuote quote, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Enable AI subtitles and set an API key first.");
        }

        if (string.IsNullOrWhiteSpace(quote.Path) || !File.Exists(quote.Path))
        {
            throw new InvalidOperationException("The video file is not on disk yet. Download it first, then create subtitles.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "tm-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var srt = await TranscribeAsync(quote, dir, cancellationToken).ConfigureAwait(false);
            if (quote.IncludesTranslation)
            {
                srt = await TranslateAsync(srt, quote.Language, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                SubtitleFiles.Write(quote.Path, quote.Language, srt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write subtitle sidecar next to {Path}", quote.Path);
            }

            return srt;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete temp subtitle dir {Dir}", dir);
            }
        }
    }

    private async Task<string> TranscribeAsync(SubtitleQuote quote, string dir, CancellationToken cancellationToken)
    {
        var next = 1;
        var parts = new System.Collections.Generic.List<string>();
        for (var chunk = 0; chunk < quote.Chunks; chunk++)
        {
            var start = TimeSpan.FromMinutes(chunk * SubtitleCost.ChunkMinutes);
            var length = TimeSpan.FromMinutes(SubtitleCost.ChunkMinutes);
            var audio = Path.Combine(dir, "chunk-" + chunk.ToString(CultureInfo.InvariantCulture) + ".mp3");
            await MediaProbe.ExtractAudioAsync(quote.Path, audio, start, length, cancellationToken).ConfigureAwait(false);
            var raw = await WhisperAsync(audio, quote.Language, cancellationToken).ConfigureAwait(false);
            var (shifted, nextIndex) = SrtCues.Shift(raw, start, next);
            next = nextIndex;
            parts.Add(shifted);
        }

        return SrtCues.Concat(parts);
    }

    private async Task<string> WhisperAsync(string audioPath, string language, CancellationToken cancellationToken)
    {
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var (url, key, model) = WhisperEndpoint(english);
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var form = new MultipartFormDataContent();
        await using var file = File.OpenRead(audioPath);
        using var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(fileContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("srt"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = form;
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Whisper returned " + (int)response.StatusCode + ": " + Truncate(body));
        }

        return body;
    }

    private async Task<string> TranslateAsync(string srt, string language, CancellationToken cancellationToken)
    {
        var chunks = SrtCues.Chunk(srt, 3500);
        var translated = new System.Collections.Generic.List<string>();
        foreach (var chunk in chunks)
        {
            var prompt = "Translate this SRT subtitle file into language code '" + language
                         + "'. Keep cue numbers and timestamps exactly. Reply with ONLY the SRT.\n\n" + chunk;
            var text = await _ai.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
            translated.Add(ExtractSrt(text) ?? chunk);
        }

        return SrtCues.Concat(translated);
    }

    private static string? ExtractSrt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf("00:", StringComparison.Ordinal);
        if (start < 0)
        {
            start = text.IndexOf("01:", StringComparison.Ordinal);
        }

        return start >= 0 ? text[Math.Max(0, text.LastIndexOf('\n', start) + 1)..].Trim() : text.Trim();
    }

    private static (string Url, string Key, string Model) WhisperEndpoint(bool englishTranslation)
    {
        var config = Config;
        var key = !string.IsNullOrWhiteSpace(config.WhisperApiKey) ? config.WhisperApiKey : config.AiApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Set a Whisper or AI API key to create subtitles.");
        }

        var model = string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel;
        // Do not reuse AiBaseUrl: Grok/Anthropic/OpenRouter chat endpoints are not Whisper.
        var root = !string.IsNullOrWhiteSpace(config.WhisperBaseUrl)
            ? config.WhisperBaseUrl.TrimEnd('/')
            : "https://api.openai.com/v1";
        if (root.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)
            || root.EndsWith("/audio/translations", StringComparison.OrdinalIgnoreCase))
        {
            return (root, key, model);
        }

        var leaf = englishTranslation ? "/audio/translations" : "/audio/transcriptions";
        return (root + leaf, key, model);
    }

    private static string Truncate(string value)
        => value.Length <= 280 ? value : value[..280];
}
