using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.Xrel;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Exposes a Treasure-Maps indexer as a browsable Jellyfin channel
/// (Trending feed and browse-by-genre), with rich movie cards.
/// </summary>
public class TreasureMapsChannel : IChannel, ISupportsLatestMedia
{
    private const string GenrePrefix = "genre:";

    private readonly TreasureMapsApiClient _client;
    private readonly XrelClient _xrel;
    private readonly ILogger<TreasureMapsChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsChannel"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="xrel">The xREL client.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsChannel(TreasureMapsApiClient client, XrelClient xrel, ILogger<TreasureMapsChannel> logger)
    {
        _client = client;
        _xrel = xrel;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Treasure-Maps";

    /// <inheritdoc />
    public string Description => "Browse movie releases from your Treasure-Maps indexer.";

    /// <inheritdoc />
    public string DataVersion
    {
        get
        {
            // Include the settings that affect the produced items so that changing them in the
            // config page invalidates Jellyfin's channel cache and triggers a re-fetch.
            var c = Config;
            return string.Join(
                '|',
                "11",
                c.PrimaryLanguage,
                string.Join(',', c.SecondaryLanguages ?? Array.Empty<string>()),
                c.FilterByLanguage ? "1" : "0",
                c.MinRating.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.ResultLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.EnableXrel ? "x1" : "x0");
        }
    }

    /// <inheritdoc />
    public string HomePageUrl => Plugin.Instance?.Configuration.BaseUrl ?? string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            MaxPageSize = Config.ResultLimit
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => TreasureMapsApiClient.IsConfigured;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(query.FolderId))
            {
                return GetRootFolders();
            }

            if (string.Equals(query.FolderId, "trending", StringComparison.Ordinal))
            {
                var trending = await _client.GetTrendingAsync(Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return await MapReleasesAsync(trending, "trending", cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(query.FolderId, "movies", StringComparison.Ordinal))
            {
                var movies = await _client.SearchMoviesAsync(null, null, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return await MapReleasesAsync(movies, "movies", cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(query.FolderId, "tv", StringComparison.Ordinal))
            {
                var tv = await _client.SearchTvAsync(null, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return await MapReleasesAsync(tv, "tv", cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(query.FolderId, "genres", StringComparison.Ordinal))
            {
                return await GetGenreFoldersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (query.FolderId.StartsWith(GenrePrefix, StringComparison.Ordinal))
            {
                var genre = query.FolderId[GenrePrefix.Length..];
                var byGenre = await _client.SearchMoviesAsync(null, genre, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return await MapReleasesAsync(byGenre, query.FolderId, cancellationToken).ConfigureAwait(false);
            }

            return new ChannelItemResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Treasure-Maps channel items for folder {FolderId}", query.FolderId);
            return new ChannelItemResult();
        }
    }

    private static ChannelItemResult GetRootFolders()
    {
        var items = new List<ChannelItemInfo>
        {
            Folder("trending", "Trending"),
            Folder("movies", "Movies"),
            Folder("tv", "TV Shows"),
            Folder("genres", "Browse by genre")
        };

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static ChannelItemInfo Folder(string id, string name) => new ChannelItemInfo
    {
        Id = id,
        Name = name,
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container
    };

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        // Surfaces a "Treasure-Maps" row of the latest trending releases on the Jellyfin home screen.
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Array.Empty<ChannelItemInfo>();
        }

        try
        {
            var trending = await _client.GetTrendingAsync(Config.ResultLimit, cancellationToken).ConfigureAwait(false);
            return (await MapReleasesAsync(trending, "latest", cancellationToken).ConfigureAwait(false)).Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load latest Treasure-Maps media");
            return Array.Empty<ChannelItemInfo>();
        }
    }

    private async Task<ChannelItemResult> GetGenreFoldersAsync(CancellationToken cancellationToken)
    {
        var caps = await _client.GetCapsAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>();
        if (caps?.Genres is not null)
        {
            foreach (var genre in caps.Genres.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = GenrePrefix + genre.Name,
                    Name = genre.Name,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container
                });
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> MapReleasesAsync(ReleaseListResponse? response, string scope, CancellationToken cancellationToken)
    {
        var prefs = GetLanguagePreferences();
        var releases = response?.Items ?? Array.Empty<Release>();
        var xrelRatings = await FetchXrelRatingsAsync(releases, cancellationToken).ConfigureAwait(false);

        // Fold the settings-dependent DataVersion into the id so a config change recreates items
        // fresh (Jellyfin does not refresh tags/overview on reused channel items).
        var marker = ShortHash(DataVersion);

        var ranked = new List<(ChannelItemInfo Item, int Rank, int Order)>();
        var order = 0;
        foreach (var release in releases)
        {
            XrelRating? xrel = null;
            if (!string.IsNullOrWhiteSpace(release.Title))
            {
                xrelRatings.TryGetValue(release.Title, out xrel);
            }

            var item = ReleaseMapper.ToChannelItem(release, Config.MinRating, prefs, xrel, out var rank);
            if (item is not null)
            {
                // Scope the item id per folder (so the same release in multiple folders is not
                // reparented/emptied) and per config marker (so settings changes refresh items).
                item.Id = scope + "|" + marker + "|" + item.Id;
                ranked.Add((item, rank, order++));
            }
        }

        // Preferred languages first (stable within the same rank).
        var items = ranked
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Order)
            .Select(x => x.Item)
            .ToList();

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<Dictionary<string, XrelRating?>> FetchXrelRatingsAsync(IReadOnlyList<Release> releases, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, XrelRating?>(StringComparer.OrdinalIgnoreCase);
        if (!XrelClient.IsEnabled)
        {
            return map;
        }

        var names = releases
            .Select(r => r.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = await Task.WhenAll(names.Select(async name =>
            (Name: name!, Rating: await _xrel.GetRatingByDirnameAsync(name, cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);

        foreach (var (name, rating) in results)
        {
            map[name] = rating;
        }

        return map;
    }

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static LanguagePreferences GetLanguagePreferences()
    {
        var config = Config;
        return new LanguagePreferences(
            config.PrimaryLanguage,
            config.SecondaryLanguages ?? Array.Empty<string>(),
            config.FilterByLanguage);
    }
}
