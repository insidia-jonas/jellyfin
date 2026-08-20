using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
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
public class TreasureMapsChannel : IChannel
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
    public string DataVersion => "3";

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
                return MapReleases(trending);
            }

            if (string.Equals(query.FolderId, "genres", StringComparison.Ordinal))
            {
                return await GetGenreFoldersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (query.FolderId.StartsWith(GenrePrefix, StringComparison.Ordinal))
            {
                var genre = query.FolderId[GenrePrefix.Length..];
                var byGenre = await _client.SearchMoviesAsync(null, genre, Config.ResultLimit, cancellationToken).ConfigureAwait(false);
                return MapReleases(byGenre);
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
            new ChannelItemInfo
            {
                Id = "trending",
                Name = "Trending",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            },
            new ChannelItemInfo
            {
                Id = "genres",
                Name = "Browse by genre",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            }
        };

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
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

    private ChannelItemResult MapReleases(ReleaseListResponse? response)
    {
        var items = new List<ChannelItemInfo>();
        if (response?.Items is not null)
        {
            foreach (var release in response.Items)
            {
                var item = ReleaseMapper.ToChannelItem(release, Config.MinRating);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }
}
