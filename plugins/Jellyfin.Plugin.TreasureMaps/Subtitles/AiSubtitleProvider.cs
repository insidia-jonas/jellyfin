using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Languages;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Native Fire TV / iOS / web subtitle search row that quotes the AI cost first.
/// Choosing the row starts Whisper (+ optional translation).
/// </summary>
public sealed class AiSubtitleProvider : ISubtitleProvider, IHasOrder
{
    private readonly AiSubtitleService _service;
    private readonly ILogger<AiSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSubtitleProvider"/> class.
    /// </summary>
    /// <param name="service">The AI subtitle service.</param>
    /// <param name="logger">The logger.</param>
    public AiSubtitleProvider(AiSubtitleService service, ILogger<AiSubtitleProvider> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Treasure-Maps KI";

    /// <inheritdoc />
    public int Order => 2;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes =>
        new[] { VideoContentType.Movie, VideoContentType.Episode };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (!AiSubtitleService.IsEnabled || string.IsNullOrWhiteSpace(request.MediaPath) || !File.Exists(request.MediaPath))
        {
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var language = ResolveLanguage(request);
        try
        {
            var quote = await _service.QuoteAsync(
                request.MediaPath,
                language,
                request.Name ?? request.SeriesName,
                request.RuntimeTicks,
                cancellationToken).ConfigureAwait(false);

            return new[]
            {
                new RemoteSubtitleInfo
                {
                    Id = EncodeId(quote),
                    ProviderName = Name,
                    Name = quote.Summary,
                    Comment = "Kosten vorher: Whisper " + SubtitleCost.FormatUsd(quote.WhisperUsd)
                              + (quote.IncludesTranslation ? " + Übersetzung " + SubtitleCost.FormatUsd(quote.TranslationUsd) : string.Empty)
                              + ". Startet erst, wenn du diesen Eintrag wählst.",
                    Format = "srt",
                    ThreeLetterISOLanguageName = OpenSubtitlesProvider.ToThreeLetter(language),
                    Author = "AI · " + quote.WhisperModel,
                    AiTranslated = quote.IncludesTranslation,
                    MachineTranslated = true,
                    DateCreated = DateTime.UtcNow
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI subtitle quote failed for {Path}", request.MediaPath);
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var quote = DecodeId(id);
        string srt;
        if (SubtitleFiles.TryRead(quote.Path, quote.Language, out var existing))
        {
            srt = existing;
        }
        else
        {
            srt = await _service.GenerateAsync(quote, cancellationToken).ConfigureAwait(false);
        }

        return new SubtitleResponse
        {
            Language = OpenSubtitlesProvider.ToThreeLetter(quote.Language),
            Format = "srt",
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(srt))
        };
    }

    public static string EncodeId(SubtitleQuote quote)
    {
        var payload = string.Join(
            "|",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(quote.Path)).Replace('+', '-').Replace('/', '_').TrimEnd('='),
            quote.Language,
            quote.Seconds.ToString("0.###", CultureInfo.InvariantCulture),
            quote.Minutes.ToString(CultureInfo.InvariantCulture),
            quote.Chunks.ToString(CultureInfo.InvariantCulture),
            quote.IncludesTranslation ? "1" : "0");
        return "ai|" + payload;
    }

    public static SubtitleQuote DecodeId(string id)
    {
        var parts = (id ?? string.Empty).Split('|');
        if (parts.Length < 6 || !string.Equals(parts[0], "ai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid AI subtitle id.");
        }

        var path = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1].Replace('-', '+').Replace('_', '/'))));
        var language = parts[2];
        var seconds = double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 60;
        var minutes = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 1;
        var chunks = int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 1;
        var translate = parts.Length > 6 && parts[6] == "1";
        return new SubtitleQuote
        {
            Path = path,
            Language = language,
            Seconds = seconds,
            Minutes = minutes,
            Chunks = chunks,
            IncludesTranslation = translate,
            WhisperUsd = 0,
            TranslationUsd = 0,
            Summary = string.Empty
        };
    }

    private static string Pad(string value)
        => value.Length % 4 == 0 ? value : value + new string('=', 4 - (value.Length % 4));

    private static string ResolveLanguage(SubtitleSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TwoLetterISOLanguageName))
        {
            return request.TwoLetterISOLanguageName.ToLowerInvariant();
        }

        var normalized = LanguageMatcher.Normalize(request.Language);
        return string.IsNullOrEmpty(normalized) ? "de" : normalized;
    }
}
