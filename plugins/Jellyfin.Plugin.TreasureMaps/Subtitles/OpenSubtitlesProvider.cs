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

        try
        {
            var results = await SearchPassesAsync(request, language, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0 && !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            {
                results = await SearchPassesAsync(request, "en", cancellationToken).ConfigureAwait(false);
            }

            return results
                .OrderByDescending(r => r.IsHashMatch == true)
                .ThenByDescending(r => r.DownloadCount ?? 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenSubtitles search failed");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }
    }

    private async Task<List<RemoteSubtitleInfo>> SearchPassesAsync(
        SubtitleSearchRequest request,
        string language,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<int>();
        var merged = new List<RemoteSubtitleInfo>();

        async Task AddAsync(Dictionary<string, string?> parameters)
        {
            parameters["languages"] = language;
            var response = await _client.SearchAsync(parameters, cancellationToken).ConfigureAwait(false);
            foreach (var mapped in (response?.Data ?? Enumerable.Empty<OsSubtitle>()).Select(Map))
            {
                if (mapped is null || !TryParseId(mapped.Id, out var fileId, out _))
                {
                    continue;
                }

                if (!seen.Add(fileId))
                {
                    continue;
                }

                merged.Add(mapped);
            }
        }

        var isEpisode = request.ContentType == VideoContentType.Episode;
        Dictionary<string, string?> Core()
        {
            var parameters = new Dictionary<string, string?>
            {
                ["type"] = isEpisode ? "episode" : "movie"
            };
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

            return parameters;
        }

        var hash = MovieHasher.ComputeHash(request.MediaPath);
        if (!string.IsNullOrEmpty(hash))
        {
            var hashed = Core();
            hashed["moviehash"] = hash;
            await AddAsync(hashed).ConfigureAwait(false);
        }

        if (request.ProviderIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            var byId = Core();
            byId["imdb_id"] = imdb.TrimStart('t', 'T');
            if (request.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
            {
                byId["tmdb_id"] = tmdb;
            }

            await AddAsync(byId).ConfigureAwait(false);
        }

        var queryText = isEpisode ? request.SeriesName : request.Name;
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            var byQuery = Core();
            byQuery["query"] = queryText;
            await AddAsync(byQuery).ConfigureAwait(false);
        }

        return merged;
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
        return string.IsNullOrEmpty(primary) ? "de" : primary;
    }

    /// <summary>
    /// Maps a 2-letter (or already 3-letter) language code to ISO 639-2.
    /// </summary>
    /// <param name="two">The language code.</param>
    /// <returns>The three-letter code.</returns>
    public static string ToThreeLetter(string two)
    {
        if (string.IsNullOrWhiteSpace(two))
        {
            return "ger";
        }

        var key = two.Trim().ToLowerInvariant();
        return _twoToThree.TryGetValue(key, out var three) ? three : key;
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
        var release = string.IsNullOrWhiteSpace(attr.Release) ? file.FileName : attr.Release;
        if (string.IsNullOrWhiteSpace(release))
        {
            release = "OpenSubtitles";
        }

        var extra = attr.DownloadCount > 0 ? " · " + attr.DownloadCount + "×" : string.Empty;
        return new RemoteSubtitleInfo
        {
            Id = EncodeId(file.FileId, lang2),
            ProviderName = "OpenSubtitles (Treasure-Maps)",
            Name = release.Trim() + extra,
            Format = "srt",
            ThreeLetterISOLanguageName = ToThreeLetter(lang2),
            DownloadCount = attr.DownloadCount,
            CommunityRating = (float)attr.Ratings,
            IsHashMatch = attr.MoviehashMatch,
            HearingImpaired = attr.HearingImpaired,
            AiTranslated = attr.AiTranslated,
            MachineTranslated = attr.MachineTranslated,
            Forced = attr.ForeignPartsOnly,
            DateCreated = DateTime.TryParse(attr.UploadDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
            Comment = CommentFor(attr, lang2)
        };
    }

    private static string? CommentFor(OsSubtitleAttributes attr, string lang2)
    {
        var bits = new List<string>();
        if (attr.MoviehashMatch)
        {
            bits.Add("Exakte Datei");
        }

        if (attr.HearingImpaired)
        {
            bits.Add("Hörgeschädigt");
        }

        if (!string.IsNullOrWhiteSpace(lang2))
        {
            bits.Add(lang2.ToUpperInvariant());
        }

        return bits.Count == 0 ? null : string.Join(" · ", bits);
    }

    private static string EncodeId(int fileId, string language)
        => fileId.ToString(CultureInfo.InvariantCulture) + "|" + language;

    /// <summary>
    /// Parses an OpenSubtitles provider id (<c>fileId|lang</c>).
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="fileId">The file id.</param>
    /// <param name="language">The language.</param>
    /// <returns>True when the id is valid.</returns>
    public static bool TryParseId(string? id, out int fileId, out string language)
    {
        var (parsedId, parsedLanguage) = DecodeId(id ?? string.Empty);
        fileId = parsedId;
        language = parsedLanguage;
        return parsedId > 0;
    }

    private static (int FileId, string Language) DecodeId(string id)
    {
        var parts = id.Split('|');
        var fileId = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var lang = parts.Length > 1 ? parts[1] : "en";
        return (fileId, lang);
    }
}
