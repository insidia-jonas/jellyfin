using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Languages;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// An OpenSubtitles subtitle provider with "intelligent" matching: exact file (movie-hash) matches
/// first, then IMDb-id/query matches, ranked by hash-match and download count.
/// </summary>
public class OpenSubtitlesProvider : ISubtitleProvider, IHasOrder
{
    private static readonly Dictionary<string, string> _twoToThree = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "ger", ["en"] = "eng", ["es"] = "spa", ["fr"] = "fre", ["it"] = "ita",
        ["nl"] = "dut", ["pt"] = "por", ["ru"] = "rus", ["ja"] = "jpn", ["zh"] = "chi",
        ["ko"] = "kor", ["pl"] = "pol", ["sv"] = "swe", ["da"] = "dan", ["no"] = "nor",
        ["fi"] = "fin", ["cs"] = "cze", ["hu"] = "hun", ["tr"] = "tur", ["ar"] = "ara"
    };

    private readonly OpenSubtitlesClient _client;
    private readonly ILogger<OpenSubtitlesProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesProvider"/> class.
    /// </summary>
    /// <param name="client">The OpenSubtitles client.</param>
    /// <param name="logger">The logger.</param>
    public OpenSubtitlesProvider(OpenSubtitlesClient client, ILogger<OpenSubtitlesProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "OpenSubtitles (Treasure-Maps)";

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes =>
        new[] { VideoContentType.Movie, VideoContentType.Episode };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (!OpenSubtitlesClient.IsEnabled)
        {
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var language = ResolveLanguage(request);
        var isEpisode = request.ContentType == VideoContentType.Episode;

        var parameters = new Dictionary<string, string?>
        {
            ["languages"] = language,
            ["type"] = isEpisode ? "episode" : "movie"
        };

        // Best signal: exact file match via movie-hash.
        var hash = MovieHasher.ComputeHash(request.MediaPath);
        if (!string.IsNullOrEmpty(hash))
        {
            parameters["moviehash"] = hash;
        }

        if (request.ProviderIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            parameters["imdb_id"] = imdb.TrimStart('t', 'T');
        }

        if (request.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            parameters["tmdb_id"] = tmdb;
        }

        var queryText = isEpisode ? request.SeriesName : request.Name;
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            parameters["query"] = queryText;
        }

        if (isEpisode)
        {
            if (request.ParentIndexNumber.HasValue)
            {
                parameters["season_number"] = request.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (request.IndexNumber.HasValue)
            {
                parameters["episode_number"] = request.IndexNumber.Value.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (request.ProductionYear.HasValue)
        {
            parameters["year"] = request.ProductionYear.Value.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            var response = await _client.SearchAsync(parameters, cancellationToken).ConfigureAwait(false);
            var results = (response?.Data ?? Enumerable.Empty<OsSubtitle>())
                .Select(Map)
                .Where(r => r is not null)
                .Select(r => r!)
                // Intelligent ranking: exact hash matches first, then most-downloaded.
                .OrderByDescending(r => r.IsHashMatch == true)
                .ThenByDescending(r => r.DownloadCount ?? 0)
                .ToList();
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenSubtitles search failed");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (fileId, language) = DecodeId(id);
        var download = await _client.RequestDownloadAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (download?.Link is null)
        {
            throw new InvalidOperationException("OpenSubtitles did not return a download link (check your login/quota).");
        }

        var bytes = await _client.DownloadContentAsync(download.Link, cancellationToken).ConfigureAwait(false);
        return new SubtitleResponse
        {
            Language = language,
            Format = "srt",
            Stream = new MemoryStream(bytes)
        };
    }

    private static string ResolveLanguage(SubtitleSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TwoLetterISOLanguageName))
        {
            return request.TwoLetterISOLanguageName.ToLowerInvariant();
        }

        var normalized = LanguageMatcher.Normalize(request.Language);
        if (!string.IsNullOrEmpty(normalized) && normalized.Length == 2)
        {
            return normalized;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var primary = LanguageMatcher.Normalize(config.PrimaryLanguage);
        return string.IsNullOrEmpty(primary) ? "en" : primary;
    }

    private static RemoteSubtitleInfo? Map(OsSubtitle sub)
    {
        var attr = sub.Attributes;
        var file = attr?.Files?.FirstOrDefault();
        if (attr is null || file is null || file.FileId <= 0)
        {
            return null;
        }

        var lang2 = (attr.Language ?? "en").ToLowerInvariant();
        return new RemoteSubtitleInfo
        {
            Id = EncodeId(file.FileId, lang2),
            ProviderName = "OpenSubtitles (Treasure-Maps)",
            Name = string.IsNullOrWhiteSpace(attr.Release) ? file.FileName : attr.Release,
            Format = "srt",
            ThreeLetterISOLanguageName = _twoToThree.TryGetValue(lang2, out var three) ? three : lang2,
            DownloadCount = attr.DownloadCount,
            CommunityRating = (float)attr.Ratings,
            IsHashMatch = attr.MoviehashMatch,
            HearingImpaired = attr.HearingImpaired,
            AiTranslated = attr.AiTranslated,
            MachineTranslated = attr.MachineTranslated,
            Forced = attr.ForeignPartsOnly,
            DateCreated = DateTime.TryParse(attr.UploadDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
            Comment = attr.MoviehashMatch ? "Exact file match" : null
        };
    }

    private static string EncodeId(int fileId, string language)
        => fileId.ToString(CultureInfo.InvariantCulture) + "|" + language;

    private static (int FileId, string Language) DecodeId(string id)
    {
        var parts = id.Split('|');
        var fileId = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var lang = parts.Length > 1 ? parts[1] : "en";
        return (fileId, lang);
    }
}
