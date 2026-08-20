using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Languages;
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
    private readonly ILogger<TreasureMapsChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsChannel"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsChannel(TreasureMapsApiClient client, ILogger<TreasureMapsChannel> logger)
    {
        _client = client;
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
                "8",
                c.PrimaryLanguage,
                string.Join(',', c.SecondaryLanguages ?? Array.Empty<string>()),
                c.FilterByLanguage ? "1" : "0",
                c.MinRating.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.ResultLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                return MapReleases(trending, "trending");
            }

            if (string.Equals(query.FolderId, "movies", StringComparison.Ordinal))
            {
                var movies = await _client.SearchMoviesAsync(null, null, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return MapReleases(movies, "movies");
            }

            if (string.Equals(query.FolderId, "tv", StringComparison.Ordinal))
            {
                var tv = await _client.SearchTvAsync(null, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return MapReleases(tv, "tv");
            }

            if (string.Equals(query.FolderId, "genres", StringComparison.Ordinal))
            {
                return await GetGenreFoldersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (query.FolderId.StartsWith(GenrePrefix, StringComparison.Ordinal))
            {
                var genre = query.FolderId[GenrePrefix.Length..];
                var byGenre = await _client.SearchMoviesAsync(null, genre, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return MapReleases(byGenre, query.FolderId);
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
            return MapReleases(trending, "latest").Items;
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

    private ChannelItemResult MapReleases(ReleaseListResponse? response, string scope)
    {
        var prefs = GetLanguagePreferences();
        var ranked = new List<(ChannelItemInfo Item, int Rank, int Order)>();
        if (response?.Items is not null)
        {
            var order = 0;
            foreach (var release in response.Items)
            {
                var item = ReleaseMapper.ToChannelItem(release, Config.MinRating, prefs, out var rank);
                if (item is not null)
                {
                    // Scope the item id per folder so the same release appearing in multiple
                    // folders (Trending/Movies/Latest) does not get reparented and emptied by Jellyfin.
                    item.Id = scope + "|" + item.Id;
                    ranked.Add((item, rank, order++));
                }
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

    private static LanguagePreferences GetLanguagePreferences()
    {
        var config = Config;
        return new LanguagePreferences(
            config.PrimaryLanguage,
            config.SecondaryLanguages ?? Array.Empty<string>(),
            config.FilterByLanguage);
    }
}
