using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
using Jellyfin.Plugin.TreasureMaps.Xrel;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Exposes a Treasure-Maps indexer as a browsable Jellyfin channel that mirrors the website:
/// Trending (Movies / TV Shows), browse Movies / TV Shows (incl. the German "DE" rows),
/// browse by genre and a Find A–Z search.
/// Titles are shown once (one poster card per movie/show); opening a card lists the individual
/// releases (qualities) behind it, which you grab by marking a release as a favorite (heart).
/// </summary>
public class TreasureMapsChannel : IChannel, ISupportsLatestMedia, IDisableMediaSourceDisplay, IRequiresMediaInfoCallback, IHasCacheKey
{
    private const string GenrePrefix = "genre:";
    private const string FindPrefix = "find:";
    private const string FeedPrefix = "tmfeed:";

    // Generation prefix for category-folder ids. Bumping it (c2-, c3-, ...) forces Jellyfin to
    // create fresh folder entities — needed once because the old entities had collage images
    // (child posters) baked in by the folder image provider, making categories look like movies.
    private const string FolderIdPrefix = "c3-";
    private const string GroupPrefix = "GRP::";
    private const string ReleasePrefix = "REL::";
    private const string GrabPrefix = "grab::";
    private const string Sep = "::";

    // The API times out above ~100 results per request, so bigger lists are fetched in pages.
    private const int PageSize = 100;
    private const int CategoryPages = 2;
    private const int FindPages = 3;
    private const int RootLatestCount = 24;

    private readonly TreasureMapsApiClient _client;
    private readonly XrelClient _xrel;
    private readonly SabnzbdClient _sabnzbd;
    private readonly GrabService _grabService;
    private readonly Recommendations.AiRecommender _ai;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly MediaBrowser.Controller.Library.IUserManager _userManager;
    private readonly ILogger<TreasureMapsChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsChannel"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="xrel">The xREL client.</param>
    /// <param name="sabnzbd">The SABnzbd client (for the Downloads folder).</param>
    /// <param name="grabService">The shared grab service (play-to-download).</param>
    /// <param name="ai">The AI recommender.</param>
    /// <param name="libraryManager">The library manager (for the user's history).</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsChannel(
        TreasureMapsApiClient client,
        XrelClient xrel,
        SabnzbdClient sabnzbd,
        GrabService grabService,
        Recommendations.AiRecommender ai,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        MediaBrowser.Controller.Library.IUserManager userManager,
        ILogger<TreasureMapsChannel> logger)
    {
        _client = client;
        _xrel = xrel;
        _sabnzbd = sabnzbd;
        _grabService = grabService;
        _ai = ai;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Treasure-Maps";

    /// <inheritdoc />
    public string Description => "Browse movies and TV shows from your Treasure-Maps indexer.";

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
                "32",
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
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie, ChannelMediaContentType.Clip },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            MaxPageSize = Config.ResultLimit
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => TreasureMapsApiClient.IsConfigured;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb };

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        var path = ChannelArtwork.GetPosterPath("channel", "Treasure-Maps");
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Path = path,
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File
        });
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var folderId = query.FolderId ?? string.Empty;
            if (folderId.StartsWith(FolderIdPrefix, StringComparison.Ordinal))
            {
                folderId = folderId[FolderIdPrefix.Length..];
            }

            if (string.IsNullOrEmpty(folderId))
            {
                return GetRoot();
            }

            if (string.Equals(folderId, "new", StringComparison.Ordinal))
            {
                return await GetRecentlyAddedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "foryou", StringComparison.Ordinal))
            {
                return await GetForYouAsync(query.UserId, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "downloads", StringComparison.Ordinal))
            {
                return await GetDownloadsAsync(cancellationToken).ConfigureAwait(false);
            }

            if (folderId.StartsWith("DL" + Sep, StringComparison.Ordinal)
                || folderId.StartsWith("dl" + Sep, StringComparison.Ordinal)
                || folderId.StartsWith("dlinfo", StringComparison.Ordinal))
            {
                return GetDownloadDetail(folderId);
            }

            // A title card (GRP) opens into the individual releases behind that movie/show.
            if (folderId.StartsWith(GroupPrefix, StringComparison.Ordinal))
            {
                return await OpenGroupAsync(folderId, cancellationToken).ConfigureAwait(false);
            }

            // A release tile (REL) is a folder too; opening it shows a small grab detail rather
            // than trying to play a non-existent stream.
            if (folderId.StartsWith(ReleasePrefix, StringComparison.Ordinal))
            {
                return GetReleaseDetail(folderId);
            }

            if (folderId.StartsWith(GrabPrefix, StringComparison.Ordinal))
            {
                return new ChannelItemResult();
            }

            if (string.Equals(folderId, "trending", StringComparison.Ordinal))
            {
                return TrendingSubFolders();
            }

            if (string.Equals(folderId, "trending-movie", StringComparison.Ordinal))
            {
                return GetSpotlightFeedFolders("movie");
            }

            if (string.Equals(folderId, "trending-tv", StringComparison.Ordinal))
            {
                return GetSpotlightFeedFolders("tv");
            }

            if (folderId.StartsWith(FeedPrefix, StringComparison.Ordinal))
            {
                return await GetSpotlightFeedAsync(folderId, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "movies", StringComparison.Ordinal))
            {
                var movies = await FetchPagesAsync("movie", null, null, null, CategoryPages, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(movies, folderId);
            }

            if (string.Equals(folderId, "tv", StringComparison.Ordinal))
            {
                var tv = await FetchPagesAsync("tv", null, null, null, CategoryPages, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(tv, folderId);
            }

            // German rows, mirroring the website's "Movies - DE" / "TV - DE" category blocks.
            if (string.Equals(folderId, "movies-de", StringComparison.Ordinal))
            {
                var moviesDe = await FetchPagesAsync("movie", null, null, TreasureMapsApiClient.GermanMovieCategories, CategoryPages, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(moviesDe, folderId);
            }

            if (string.Equals(folderId, "tv-de", StringComparison.Ordinal))
            {
                var tvDe = await FetchPagesAsync("tv", null, null, TreasureMapsApiClient.GermanTvCategories, CategoryPages, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(tvDe, folderId);
            }

            if (string.Equals(folderId, "genres", StringComparison.Ordinal))
            {
                return await GetGenreFoldersAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "find", StringComparison.Ordinal))
            {
                return GetLetterFolders();
            }

            if (folderId.StartsWith(FindPrefix, StringComparison.Ordinal))
            {
                return await SearchByLetterAsync(folderId[FindPrefix.Length..], cancellationToken).ConfigureAwait(false);
            }

            if (folderId.StartsWith(GenrePrefix, StringComparison.Ordinal))
            {
                var byGenre = await FetchPagesAsync("movie", null, folderId[GenrePrefix.Length..], null, CategoryPages, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(byGenre, folderId);
            }

            return new ChannelItemResult();
        }
        catch (Exception ex)
        {
            // Rethrow instead of returning an empty result: Jellyfin caches whatever a channel
            // folder returns (for hours), so a transient indexer failure returned as "empty"
            // would freeze the folder as blank until the next cache invalidation.
            _logger.LogError(ex, "Failed to load Treasure-Maps channel items for folder {FolderId}", query.FolderId);
            throw;
        }
    }

    /// <summary>
    /// Builds the channel root. It contains ONLY category folders on purpose: clients offer many
    /// sort modes (name, date added, random, ...) and any grid that mixes category folders with
    /// title cards will scatter the folders between the posters under some of them. The recently
    /// added titles live behind the "Recently added" folder (and in the home-screen Latest row).
    /// </summary>
    private static ChannelItemResult GetRoot()
    {
        var items = new List<ChannelItemInfo>
        {
            Folder("foryou", "For You", 0),
            Folder("downloads", "Downloads", 1),
            Folder("new", "Recently added", 2),
            Folder("trending", "Trending", 3),
            Folder("movies", "Movies", 4),
            Folder("tv", "TV Shows", 5),
            Folder("movies-de", "Movies (DE)", 6),
            Folder("tv-de", "TV Shows (DE)", 7),
            Folder("genres", "Browse by genre", 8),
            Folder("find", "Find A\u2013Z", 9)
        };

        return Result(items);
    }

    /// <summary>
    /// Builds the AI-powered "For You" view: collects the user's watch/favorite history, asks the
    /// configured LLM provider for personalized recommendations, and resolves each recommendation
    /// against the indexer as a normal title card (with the AI's reason in the overview).
    /// </summary>
    private async Task<ChannelItemResult> GetForYouAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!Recommendations.AiRecommender.IsEnabled)
        {
            var hint = Folder("foryou-hint", "Enable AI recommendations in the Treasure-Maps plugin settings", 0);
            hint.Overview = "Set an AI provider (Grok / OpenAI / Anthropic) and API key on the plugin configuration page to get personal recommendations here.";
            return Result(new List<ChannelItemInfo> { hint });
        }

        var (watched, favorites) = CollectHistory(userId);
        var recommendations = await _ai.GetRecommendationsAsync(
            userId.ToString("N"),
            watched,
            favorites,
            15,
            cancellationToken).ConfigureAwait(false);

        if (recommendations.Count == 0)
        {
            throw new InvalidOperationException("The AI provider returned no usable recommendations.");
        }

        // Resolve every recommendation against the indexer in parallel; unavailable titles are skipped.
        var resolved = await Task.WhenAll(recommendations.Select(r => ResolveRecommendationAsync(r, cancellationToken))).ConfigureAwait(false);
        var items = resolved.Where(i => i is not null).Select(i => i!).ToList();
        return Result(items);
    }

    /// <summary>
    /// Collects the user's history: recently watched movies/series and favorites (including
    /// favorited Treasure-Maps title cards, i.e. downloads).
    /// </summary>
    private (IReadOnlyList<string> Watched, IReadOnlyList<string> Favorites) CollectHistory(Guid userId)
    {
        var user = userId.Equals(default) ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var watchedQuery = new MediaBrowser.Controller.Entities.InternalItemsQuery(user)
        {
            IsPlayed = true,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode },
            OrderBy = new[] { (Jellyfin.Data.Enums.ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) },
            Limit = 80,
            Recursive = true
        };
        var watchedItems = _libraryManager.GetItemsResult(watchedQuery).Items;

        var watched = new List<string>();
        foreach (var item in watchedItems)
        {
            var label = item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? (episode.SeriesName ?? episode.Name) + " [tv]"
                : item.Name + (item.ProductionYear.HasValue ? $" ({item.ProductionYear})" : string.Empty) + " [movie]";
            if (!watched.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                watched.Add(label);
            }

            if (watched.Count >= 40)
            {
                break;
            }
        }

        var favoriteQuery = new MediaBrowser.Controller.Entities.InternalItemsQuery(user)
        {
            IsFavorite = true,
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Series,
                Jellyfin.Data.Enums.BaseItemKind.BoxSet
            },
            Limit = 40,
            Recursive = true
        };
        var favorites = _libraryManager.GetItemsResult(favoriteQuery).Items
            .Select(i => i.Name + (i.ProductionYear.HasValue ? $" ({i.ProductionYear})" : string.Empty))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (watched, favorites);
    }

    /// <summary>
    /// Resolves one AI recommendation against the indexer and builds its title card.
    /// </summary>
    private async Task<ChannelItemInfo?> ResolveRecommendationAsync(Recommendations.AiRecommendation rec, CancellationToken cancellationToken)
    {
        try
        {
            var kind = string.Equals(rec.Type, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
            var (releases, ok) = await FetchPageSafeAsync(kind, rec.Title, null, null, 0, cancellationToken).ConfigureAwait(false);
            if (!ok || releases.Count == 0)
            {
                return null;
            }

            var wanted = NormalizeTitle(rec.Title);
            var matching = releases
                .Where(r =>
                {
                    var title = NormalizeTitle(ReleaseGrouper.TitleOf(r, ReleaseGrouper.KindOf(r)));
                    return title.Length > 0 && (title == wanted || title.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(title, StringComparison.Ordinal));
                })
                .ToList();
            if (matching.Count == 0)
            {
                return null;
            }

            var card = BuildGroupCards(matching, "foryou").Items.FirstOrDefault();
            if (card is not null && !string.IsNullOrWhiteSpace(rec.Reason))
            {
                card.Overview = "\u2728 " + rec.Reason + "\n\n" + (card.Overview ?? string.Empty);
            }

            return card;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve AI recommendation {Title}", rec.Title);
            return null;
        }
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the Downloads view: only jobs this plugin queued (not the rest of SABnzbd),
    /// as openable title cards with the movie name and poster — not quality-string folders.
    /// </summary>
    private async Task<ChannelItemResult> GetDownloadsAsync(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            var hint = Folder("downloads-hint", "Configure SABnzbd in the Treasure-Maps plugin settings", 0);
            return Result(new List<ChannelItemInfo> { hint });
        }

        var (speed, entries) = await _sabnzbd.GetDownloadStatusAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>();
        var order = 0;
        foreach (var entry in entries)
        {
            if (!_grabService.IsTracked(entry.Id, entry.Name))
            {
                continue;
            }

            items.Add(DownloadCard(entry, order++, speed));
        }

        if (items.Count == 0)
        {
            var empty = Folder("downloads-empty", "No Treasure-Maps downloads yet", 0);
            empty.Overview = "Only movies and shows you grab from Treasure-Maps appear here. Other SABnzbd jobs stay in your download client.";
            items.Add(empty);
        }

        return Result(items);
    }

    private ChannelItemInfo DownloadCard(SabnzbdClient.SabDownloadStatus entry, int order, string? speed)
    {
        var rec = _grabService.Lookup(entry.Id, entry.Name);
        var title = DownloadTitle.Resolve(entry.Name, rec?.Title);
        var quality = rec?.Quality;
        if (string.IsNullOrWhiteSpace(quality) && DownloadTitle.LooksLikeQualityLabel(entry.Name))
        {
            quality = entry.Name;
        }

        var active = !string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase);
        var overview = DownloadOverview(entry, speed, quality);
        var cover = rec?.CoverUrl ?? _grabService.GetArtwork(entry.Id, entry.Name);
        if (string.IsNullOrWhiteSpace(cover))
        {
            cover = ChannelArtwork.GetPosterPath("dl-" + (entry.Id ?? title), title);
        }

        var card = new ChannelItemInfo
        {
            Id = string.Join(Sep, "DL", entry.Id ?? order.ToString(System.Globalization.CultureInfo.InvariantCulture), Encode(title)),
            Name = title,
            OriginalTitle = title,
            SortName = ChannelPresentation.DownloadSortName(active, title),
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.BoxSet,
            ContentType = string.Equals(rec?.Kind, "tv", StringComparison.Ordinal) ? ChannelMediaContentType.TvExtra : ChannelMediaContentType.Movie,
            ImageUrl = cover,
            Overview = overview,
            DateCreated = rec?.GrabbedAt is DateTime grabbed && grabbed != default ? grabbed : DateTime.UtcNow.AddMinutes(-order)
        };

        if (!string.IsNullOrWhiteSpace(rec?.Guid))
        {
            card.ProviderIds["TreasureMaps"] = rec.Guid;
        }

        if (!string.IsNullOrWhiteSpace(rec?.Kind))
        {
            card.ProviderIds["TreasureMapsKind"] = rec.Kind;
        }

        card.ProviderIds["TreasureMapsTitle"] = title;
        if (!string.IsNullOrWhiteSpace(quality))
        {
            card.ProviderIds["TreasureMapsQuality"] = quality;
        }

        return card;
    }

    private static string DownloadOverview(SabnzbdClient.SabDownloadStatus entry, string? speed, string? quality)
    {
        var badge = string.IsNullOrWhiteSpace(quality) ? string.Empty : quality.Trim() + "\n\n";
        if (string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return badge + "Download complete. Open Movies or TV Shows once the library scan finishes.";
        }

        if (string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return badge + "Download failed." + (string.IsNullOrWhiteSpace(entry.FailMessage) ? string.Empty : " " + entry.FailMessage);
        }

        return badge + $"Downloading \u2013 {entry.Percent:0}%"
            + (string.IsNullOrWhiteSpace(speed) ? string.Empty : $" \u00B7 {speed}B/s")
            + (string.IsNullOrWhiteSpace(entry.TimeLeft) ? string.Empty : $" \u00B7 {entry.TimeLeft} left")
            + (string.IsNullOrWhiteSpace(entry.LeftMb) ? string.Empty : $" \u00B7 {entry.LeftMb}/{entry.SizeMb} MB remaining");
    }

    private ChannelItemResult GetDownloadDetail(string folderId)
    {
        if (folderId.StartsWith("dlinfo", StringComparison.Ordinal))
        {
            var done = new ChannelItemInfo
            {
                Id = FolderIdPrefix + "downloads-library-hint",
                Name = "Finished downloads appear in Movies / TV Shows",
                SortName = ChannelPresentation.FolderSortName(0, "Finished"),
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Treasure-Maps sends the NZB to SABnzbd. After it finishes, the file is scanned into your Movies or TV Shows library.",
                DateCreated = DateTime.UtcNow
            };
            return Result(new List<ChannelItemInfo> { done });
        }

        var parts = folderId.Split(Sep);
        var nzoId = parts.Length > 1 ? parts[1] : string.Empty;
        var title = parts.Length > 2 ? Decode(parts[2]) : "Download";
        var rec = _grabService.Lookup(nzoId, title);
        if (!string.IsNullOrWhiteSpace(rec?.Title))
        {
            title = rec.Title;
        }

        var cover = rec?.CoverUrl;
        if (string.IsNullOrWhiteSpace(cover))
        {
            cover = ChannelArtwork.GetPosterPath("dl-" + (nzoId.Length > 0 ? nzoId : title), title);
        }

        var quality = rec?.Quality;
        var child = new ChannelItemInfo
        {
            Id = "dlinfo" + Sep + (nzoId.Length > 0 ? nzoId : Encode(title)),
            Name = string.IsNullOrWhiteSpace(quality) ? "Download status" : quality,
            OriginalTitle = title,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            ImageUrl = cover,
            Overview = string.IsNullOrWhiteSpace(rec?.Quality)
                ? "This is a Treasure-Maps download, not a stream. After SABnzbd finishes, open Movies or TV Shows."
                : rec.Quality + "\n\nThis is a Treasure-Maps download, not a stream. After SABnzbd finishes, open Movies or TV Shows.",
            DateCreated = rec?.GrabbedAt is DateTime grabbed && grabbed != default ? grabbed : DateTime.UtcNow
        };
        child.ProviderIds["TreasureMapsTitle"] = title;
        if (!string.IsNullOrWhiteSpace(rec?.Guid))
        {
            child.ProviderIds["TreasureMaps"] = rec.Guid;
        }

        return Result(new List<ChannelItemInfo> { child });
    }

    /// <inheritdoc />
    public string? GetCacheKey(string? userId)
    {
        // Jellyfin caches channel folder results on disk for hours; folding a 2-minute time
        // bucket into the key keeps the Downloads view (progress in names) reasonably live.
        // The plugin's own in-memory API caches keep this cheap for the indexer.
        var bucket = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(2).Ticks;
        return (userId ?? string.Empty) + "-" + DataVersion + "-" + bucket.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Play-to-download: the grab entry below a release is a playable clip. Starting playback
    /// (the native Play button on Fire TV/any client) kicks off the SABnzbd grab in the
    /// background and plays a short bundled "Download started" confirmation video.
    /// </summary>
    /// <param name="id">The channel item external id (<c>grab::&lt;kind&gt;::&lt;guid&gt;::&lt;name&gt;</c>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmation clip media source.</returns>
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (id.StartsWith(GrabPrefix, StringComparison.Ordinal))
        {
            // grab::<kind>::<guid>::<b64 title>::<b64 quality>::<b64 cover>
            // older: grab::<kind>::<guid>::<b64 name>::<b64 cover>
            var parts = id.Split(Sep);
            var kind = parts.Length > 1 ? parts[1] : "movie";
            var guid = parts.Length > 2 ? parts[2] : string.Empty;
            string title;
            string? quality;
            string? cover;
            if (parts.Length >= 6)
            {
                title = Decode(parts[3]);
                quality = Decode(parts[4]);
                cover = Decode(parts[5]);
            }
            else
            {
                var raw = parts.Length > 3 ? Decode(parts[3]) : guid;
                cover = parts.Length > 4 ? Decode(parts[4]) : null;
                if (DownloadTitle.LooksLikeQualityLabel(raw))
                {
                    quality = raw;
                    title = string.Empty;
                }
                else
                {
                    title = raw;
                    quality = null;
                }
            }

            var jobName = string.IsNullOrWhiteSpace(title) ? (quality ?? guid) : title;

            if (!string.IsNullOrEmpty(guid))
            {
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await _grabService.GrabAsync(
                                guid,
                                jobName,
                                string.Equals(kind, "tv", StringComparison.Ordinal),
                                cover,
                                CancellationToken.None,
                                title,
                                quality).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Play-to-download grab failed for {Guid}", guid);
                        }
                    },
                    CancellationToken.None);
            }
        }

        var clip = Media.ConfirmationClip.GetPath(_logger);
        var source = new MediaSourceInfo
        {
            Id = "tm-confirmation",
            Name = "Download started",
            Path = clip,
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File,
            Container = "mp4",
            IsRemote = false,
            VideoType = VideoType.VideoFile,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Bitrate = 150000,
            RunTimeTicks = TimeSpan.FromSeconds(6).Ticks,
            // Declared stream info lets clients direct-play the clip (h264/aac in mp4 plays
            // natively everywhere incl. Fire TV); without it the transcode path is hit.
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Profile = "High",
                    Level = 31,
                    Width = 1280,
                    Height = 720,
                    RealFrameRate = 24,
                    AverageFrameRate = 24,
                    BitRate = 120000,
                    IsDefault = true
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 44100,
                    BitRate = 32000,
                    IsDefault = true
                }
            }
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
    }

    /// <summary>
    /// Builds the "Recently added" view: the newest movie + TV titles, one poster card per title.
    /// Fetches are fault-tolerant per kind (the indexer rate-limits with 503s); the view only
    /// errors (and is not cached empty) when both kinds fail.
    /// </summary>
    private async Task<ChannelItemResult> GetRecentlyAddedAsync(CancellationToken cancellationToken)
    {
        var moviesTask = FetchPageSafeAsync("movie", null, null, null, 0, cancellationToken);
        var tvTask = FetchPageSafeAsync("tv", null, null, null, 0, cancellationToken);
        var both = await Task.WhenAll(moviesTask, tvTask).ConfigureAwait(false);

        if (both.All(r => !r.Ok))
        {
            throw new InvalidOperationException("Both the movie and TV feeds failed for the Recently added view.");
        }

        var releases = both.SelectMany(r => r.Items).ToList();
        var cards = BuildGroupCards(releases, "new");
        var items = cards.Items.Take(RootLatestCount).ToList();
        return Result(items);
    }

    /// <summary>
    /// Fetches multiple result pages (the API caps a single request at ~100 items) and
    /// concatenates them, newest first. Pages are fetched independently: a slow/failed page
    /// (cold single-letter queries can 504 on the indexer side) must not blank the whole view.
    /// </summary>
    private async Task<IReadOnlyList<Release>> FetchPagesAsync(string kind, string? query, string? genre, string? categories, int pages, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<(IReadOnlyList<Release> Items, bool Ok)>>(pages);
        for (var page = 0; page < pages; page++)
        {
            tasks.Add(FetchPageSafeAsync(kind, query, genre, categories, page * PageSize, cancellationToken));
        }

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);

        // If every page failed the view must error (and NOT get cached as empty by Jellyfin).
        if (responses.All(r => !r.Ok))
        {
            throw new InvalidOperationException($"All Treasure-Maps {kind} pages failed (q={query ?? "*"}).");
        }

        return responses.SelectMany(r => r.Items).ToList();
    }

    private async Task<(IReadOnlyList<Release> Items, bool Ok)> FetchPageSafeAsync(string kind, string? query, string? genre, string? categories, int offset, CancellationToken cancellationToken)
    {
        try
        {
            var response = string.Equals(kind, "tv", StringComparison.Ordinal)
                ? await _client.SearchTvAsync(query, categories, PageSize, offset, cancellationToken).ConfigureAwait(false)
                : await _client.SearchMoviesAsync(query, genre, categories, PageSize, offset, cancellationToken).ConfigureAwait(false);
            return (response?.Items ?? Array.Empty<Release>(), true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps {Kind} page at offset {Offset} failed (q={Query}); continuing with the other pages", kind, offset, query);
            return (Array.Empty<Release>(), false);
        }
    }

    private static ChannelItemResult TrendingSubFolders()
        => Result(new List<ChannelItemInfo> { Folder("trending-movie", "Movies", 0), Folder("trending-tv", "TV Shows", 1) });

    private static ChannelItemResult GetLetterFolders()
    {
        var items = new List<ChannelItemInfo> { Folder(FindPrefix + "0-9", "0-9", 0) };
        for (var c = 'A'; c <= 'Z'; c++)
        {
            items.Add(Folder(FindPrefix + c, c.ToString(), c - 'A' + 1));
        }

        return Result(items);
    }

    private async Task<ChannelItemResult> SearchByLetterAsync(string token, CancellationToken cancellationToken)
    {
        // Live search against the indexer, then keep only titles whose (article-stripped) name
        // starts with the chosen letter/digit — a TV-friendly way to look up a specific title.
        // A single-letter query is a broad substring search, so several pages are fetched and
        // prefix-filtered to fill the letter folder with the most recently added matches.
        var q = string.Equals(token, "0-9", StringComparison.Ordinal) ? null : token;
        var moviesTask = FetchPagesAsync("movie", q, null, null, FindPages, cancellationToken);
        var tvTask = FetchPagesAsync("tv", q, null, null, FindPages, cancellationToken);
        var both = await Task.WhenAll(moviesTask, tvTask).ConfigureAwait(false);

        var releases = both[0]
            .Concat(both[1])
            .Where(r => StartsWithToken(ResolveTitle(r), token))
            .ToList();

        return BuildGroupCards(releases, FindPrefix + token);
    }

    private static string ResolveTitle(Release release)
    {
        var kind = ReleaseGrouper.KindOf(release);
        return ReleaseGrouper.TitleOf(release, kind);
    }

    private static bool StartsWithToken(string title, string token)
    {
        var t = StripArticle(title).TrimStart();
        if (t.Length == 0)
        {
            return false;
        }

        if (string.Equals(token, "0-9", StringComparison.Ordinal))
        {
            return char.IsDigit(t[0]);
        }

        return t.StartsWith(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripArticle(string title)
    {
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return title[article.Length..];
            }
        }

        return title;
    }

    /// <summary>
    /// Builds one poster card per movie/show (grouping the releases behind it). The scope is
    /// folded into each card id so the same title appearing in several folders (root, Movies,
    /// Movies (DE), a letter, ...) yields distinct channel items — otherwise Jellyfin reparents
    /// the shared item and the other folders appear empty.
    /// </summary>
    private ChannelItemResult BuildGroupCards(IReadOnlyList<Release>? releases, string scope)
    {
        var prefs = GetLanguagePreferences();
        var marker = ShortHash(DataVersion);
        var scopeHash = ShortHash(scope);

        // Apply the language/rating filter (ToChannelItem returns null when filtered out) and keep
        // the best language rank per title so preferred-language titles sort first.
        var kept = new List<Release>();
        var bestRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases ?? Array.Empty<Release>())
        {
            var tile = ReleaseMapper.ToChannelItem(release, Config.MinRating, prefs, null, out var rank);
            if (tile is null)
            {
                continue;
            }

            kept.Add(release);
            var key = ReleaseGrouper.KeyOf(release);
            if (!bestRank.TryGetValue(key, out var existing) || rank < existing)
            {
                bestRank[key] = rank;
            }
        }

        var groups = ReleaseGrouper.Group(kept);
        var ordered = groups
            .OrderBy(g => bestRank.TryGetValue(g.Key, out var r) ? r : int.MaxValue)
            .Take(Config.ResultLimit)
            .ToList();

        var items = new List<ChannelItemInfo>(ordered.Count);
        foreach (var group in ordered)
        {
            var count = group.Releases.Count;

            // The card's cover URL travels inside the id so that opening the card can show the
            // exact same poster on every release tile (covers can differ between releases).
            // FolderType BoxSet makes clients open a DETAILS page (plot, rating, genres, cast,
            // IMDb link) with the releases listed below, instead of a bare children list.
            var card = new ChannelItemInfo
            {
                Id = string.Join(Sep, GroupPrefix.TrimEnd(':'), scopeHash, marker, group.Kind, Encode(group.Key), Encode(group.Title), Encode(group.Cover ?? string.Empty)),
                Name = group.Title,
                OriginalTitle = group.Title,
                SortName = ChannelPresentation.TitleSortName(group.Posted, group.Title),
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.BoxSet,
                ContentType = string.Equals(group.Kind, "tv", StringComparison.Ordinal) ? ChannelMediaContentType.TvExtra : ChannelMediaContentType.Movie,
                ImageUrl = group.Cover,
                ProductionYear = group.Year,
                PremiereDate = group.Year is >= 1900 and <= 2100 ? new DateTime(group.Year.Value, 1, 1) : null,
                CommunityRating = group.Rating.HasValue ? (float)group.Rating.Value : null,
                // Real posted date so the client's "Date added" sort shows the newest titles
                // first (and never above the future-pinned category folders).
                DateCreated = group.Posted?.UtcDateTime
            };

            var hint = count + (count == 1 ? " release available." : " releases available.")
                + " Mark a release below as a favorite (\u2764) to download it.";
            card.Overview = string.IsNullOrWhiteSpace(group.Plot) ? hint : group.Plot + "\n\n" + hint;
            if (!string.IsNullOrWhiteSpace(group.Tagline))
            {
                card.Overview = group.Tagline + "\n\n" + card.Overview;
            }

            if (group.Genres.Count > 0)
            {
                card.Genres = group.Genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            foreach (var actor in group.Actors.Take(10))
            {
                card.People.Add(new MediaBrowser.Controller.Entities.PersonInfo { Name = actor, Type = Jellyfin.Data.Enums.PersonKind.Actor });
            }

            // The IMDb id gives the details page its IMDb link. (No TMDB id here: on a BoxSet it
            // would render a themoviedb.org/collection/... link, which is wrong for a movie.)
            if (!string.IsNullOrWhiteSpace(group.Imdb))
            {
                card.ProviderIds["Imdb"] = ReleaseMapper.NormalizeImdbId(group.Imdb!);
            }

            // Favouriting the title card (the poster) grabs the best release in the group.
            var best = ReleaseGrouper.PickBestRelease(group.Releases);
            if (best is not null && !string.IsNullOrWhiteSpace(best.Guid))
            {
                card.ProviderIds["TreasureMaps"] = best.Guid;
                card.ProviderIds["TreasureMapsKind"] = group.Kind;
            }

            card.Tags.Add(count == 1 ? "1 release" : count + " releases");
            items.Add(card);
        }

        return Result(items);
    }

    /// <summary>
    /// Opens a title card: re-fetches the title's releases and lists them as grabbable tiles.
    /// </summary>
    private async Task<ChannelItemResult> OpenGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        // GRP::<scopeHash>::<marker>::<kind>::<key>::<title>::<cover>
        var parts = groupId.Split(Sep);
        if (parts.Length < 6)
        {
            return new ChannelItemResult();
        }

        var kind = parts[3];
        var key = Decode(parts[4]);
        var title = Decode(parts[5]);
        var cover = parts.Length >= 7 ? Decode(parts[6]) : string.Empty;

        var response = string.Equals(kind, "tv", StringComparison.Ordinal)
            ? await _client.SearchTvAsync(title, PageSize, cancellationToken).ConfigureAwait(false)
            : await _client.SearchMoviesAsync(title, null, PageSize, cancellationToken).ConfigureAwait(false);

        var all = response?.Items ?? Array.Empty<Release>();
        var matching = all.Where(r => string.Equals(ReleaseGrouper.KeyOf(r), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
        {
            matching = all.ToList();
        }

        return await BuildReleaseTilesAsync(matching, groupId, cover, title, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the individual release tiles (one per release/quality) shown inside a title card.
    /// </summary>
    private async Task<ChannelItemResult> BuildReleaseTilesAsync(IReadOnlyList<Release> releases, string scope, string? groupCover, string groupTitle, CancellationToken cancellationToken)
    {
        var prefs = GetLanguagePreferences();
        var xrelRatings = await FetchXrelRatingsAsync(releases, cancellationToken).ConfigureAwait(false);
        var marker = ShortHash(DataVersion);
        var scopeHash = ShortHash(scope);

        var ranked = new List<(ChannelItemInfo Item, int Rank, int Quality, int Order)>();
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
                var kind = item.ProviderIds.TryGetValue("TreasureMapsKind", out var k) && !string.IsNullOrEmpty(k) ? k : "movie";

                // Inside a title card, name the tile by its audio-language flags plus quality
                // badges (resolution, source, codec, language, size, group) so the qualities are
                // told apart at a glance. The full scene name stays in the tile's overview.
                var parsed = ReleaseNameParser.Parse(release.Title);
                var flags = ReleaseMapper.LanguageFlags(release);
                var label = ReleaseMapper.BuildQualityLabel(release);
                item.Name = string.IsNullOrEmpty(flags) ? label : flags + " " + label;

                // All tiles of a title share the card's poster so the card and its releases look alike.
                if (!string.IsNullOrWhiteSpace(groupCover))
                {
                    item.ImageUrl = groupCover;
                }

                // REL::<scope>::<marker>::<kind>::<guid>::<b64 title>::<b64 quality>::<b64 cover>
                item.Id = string.Join(
                    Sep,
                    ReleasePrefix.TrimEnd(':'),
                    scopeHash,
                    marker,
                    kind,
                    item.Id,
                    Encode(groupTitle),
                    Encode(item.Name),
                    Encode(item.ImageUrl ?? string.Empty));
                item.OriginalTitle = groupTitle;
                item.ProviderIds["TreasureMapsTitle"] = groupTitle;
                item.ProviderIds["TreasureMapsQuality"] = item.Name;
                ranked.Add((item, rank, parsed.QualityScore, order++));
            }
        }

        var items = ranked
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => x.Quality)
            .ThenBy(x => x.Order)
            .Select(x => x.Item)
            .Take(Config.ResultLimit)
            .ToList();

        return Result(items);
    }

    private static ChannelItemResult GetReleaseDetail(string folderId)
    {
        // REL::<scope>::<marker>::<kind>::<guid>::<b64 title>::<b64 quality>::<b64 cover>
        // older: REL::<scope>::<marker>::<kind>::<guid>::<b64 name>::<b64 cover>
        var parts = folderId.Split(Sep);
        if (parts.Length < 5)
        {
            return new ChannelItemResult();
        }

        var kind = string.Equals(parts[3], "tv", StringComparison.Ordinal) ? "tv" : "movie";
        var guid = parts[4];
        string title;
        string quality;
        string cover;
        if (parts.Length >= 8)
        {
            title = Decode(parts[5]);
            quality = Decode(parts[6]);
            cover = Decode(parts[7]);
        }
        else
        {
            quality = parts.Length >= 6 ? Decode(parts[5]) : string.Empty;
            cover = parts.Length >= 7 ? Decode(parts[6]) : string.Empty;
            title = DownloadTitle.LooksLikeQualityLabel(quality) ? string.Empty : quality;
        }

        // A playable clip: pressing the native PLAY button (Fire TV etc.) starts the download and
        // plays a short confirmation video (see GetChannelItemMediaInfo). Favoriting still works.
        var child = new ChannelItemInfo
        {
            Id = string.Join(Sep, GrabPrefix.TrimEnd(':'), kind, guid, Encode(title), Encode(quality), Encode(cover)),
            Name = "\u2B07 Start download",
            OriginalTitle = string.IsNullOrWhiteSpace(title) ? quality : title,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Clip,
            MediaType = ChannelMediaType.Video,
            ImageUrl = string.IsNullOrEmpty(cover) ? null : cover,
            Overview = "Press Play to send this release to your download client (SABnzbd). A short confirmation clip plays, and the progress appears in the Downloads folder. Marking as favorite (\u2764) works too."
        };
        child.ProviderIds["TreasureMaps"] = guid;
        child.ProviderIds["TreasureMapsKind"] = kind;
        if (!string.IsNullOrWhiteSpace(title))
        {
            child.ProviderIds["TreasureMapsTitle"] = title;
        }

        if (!string.IsNullOrWhiteSpace(quality))
        {
            child.ProviderIds["TreasureMapsQuality"] = quality;
        }

        return Result(new List<ChannelItemInfo> { child });
    }

    // Category folders are pinned to the top for BOTH sort modes clients use: the "# " name
    // prefix wins the (default) SortName sort, and a far-future, per-index staggered DateCreated
    // wins the "Date added" (descending) sort while also fixing the folders' relative order.
    private static readonly DateTime _folderPinBase = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChannelItemInfo Folder(string id, string name, int order = 0) => new ChannelItemInfo
    {
        Id = FolderIdPrefix + id,
        Name = name,
        SortName = ChannelPresentation.FolderSortName(order, name),
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container,
        DateCreated = _folderPinBase.AddMinutes(-order),
        ImageUrl = ChannelArtwork.GetPosterPath(id, name),
        Overview = name
    };

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Array.Empty<ChannelItemInfo>();
        }

        try
        {
            var movies = await _client.SearchMoviesAsync(null, null, PageSize, cancellationToken).ConfigureAwait(false);
            return BuildGroupCards(movies?.Items, "latest").Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load latest Treasure-Maps media");
            return Array.Empty<ChannelItemInfo>();
        }
    }

    /// <summary>
    /// Lists the website's TMDB spotlight feed rows for movies or TV (same rows as on the
    /// Treasure-Maps homepage: popular, trending today/this week, top rated, in cinema/on air).
    /// </summary>
    private static ChannelItemResult GetSpotlightFeedFolders(string kind)
    {
        var names = string.Equals(kind, "tv", StringComparison.Ordinal)
            ? new[] { "Beliebte Serien", "Trending Heute", "Trending Diese Woche", "Top Rated Serien", "Aktuell im TV" }
            : new[] { "Beliebt auf TMDB", "Trending Heute", "Trending Diese Woche", "Top Rated", "Jetzt im Kino" };

        var items = new List<ChannelItemInfo>(names.Length);
        for (var feed = 1; feed <= names.Length; feed++)
        {
            items.Add(Folder(FeedPrefix + kind + ":" + feed.ToString(System.Globalization.CultureInfo.InvariantCulture), names[feed - 1], feed - 1));
        }

        return Result(items);
    }

    /// <summary>
    /// Builds one spotlight feed row: fetches the feed (titles that have releases), enriches each
    /// item with poster/metadata (the feed only carries an imdb id, no covers) and groups them
    /// into one card per title.
    /// </summary>
    private async Task<ChannelItemResult> GetSpotlightFeedAsync(string folderId, CancellationToken cancellationToken)
    {
        // tmfeed:<kind>:<feed>
        var parts = folderId.Split(':');
        var kind = parts.Length > 1 && string.Equals(parts[1], "tv", StringComparison.Ordinal) ? "tv" : "movie";
        var feed = parts.Length > 2 && int.TryParse(parts[2], out var f) ? f : 1;

        var response = await _client.GetSpotlightAsync(kind, feed, 30, cancellationToken).ConfigureAwait(false);
        var items = response?.Items ?? Array.Empty<Release>();

        var enriched = await Task.WhenAll(items.Select(it => EnrichTrendingAsync(it, kind, cancellationToken))).ConfigureAwait(false);
        var releases = enriched.Where(r => r is not null).Select(r => r!).ToList();
        return BuildGroupCards(releases, folderId);
    }

    private async Task<Release?> EnrichTrendingAsync(Release item, string kind, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.Images?.Cover))
            {
                return item;
            }

            var title = string.Equals(kind, "tv", StringComparison.Ordinal)
                ? ReleaseGrouper.ShowNameFromScene(item.Title)
                : ReleaseGrouper.CleanSceneTitle(item.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                return item;
            }

            var response = string.Equals(kind, "tv", StringComparison.Ordinal)
                ? await _client.SearchTvAsync(title, 10, cancellationToken).ConfigureAwait(false)
                : await _client.SearchMoviesAsync(title, null, 10, cancellationToken).ConfigureAwait(false);
            var results = response?.Items ?? Array.Empty<Release>();

            var imdb = item.Ids?.Imdb;
            Release? match = null;
            if (!string.IsNullOrWhiteSpace(imdb))
            {
                match = results.FirstOrDefault(r => string.Equals(r.Ids?.Imdb ?? r.Tv?.Imdb, imdb, StringComparison.Ordinal));
            }

            match ??= results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Images?.Cover));
            return match ?? item;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich trending item {Title}", item.Title);
            return item;
        }
    }

    private async Task<ChannelItemResult> GetGenreFoldersAsync(CancellationToken cancellationToken)
    {
        // The indexer's caps expose thousands of raw library tags (incl. adult ones); only the
        // curated common-genre whitelist is surfaced here.
        var caps = await _client.GetCapsAsync(cancellationToken).ConfigureAwait(false);
        var available = (caps?.Genres ?? new List<CapsNamedItem>()).Select(g => g.Name);
        var items = CommonGenres.FilterAvailable(available)
            .Select((name, index) => Folder(GenrePrefix + name, name, index))
            .ToList();

        return Result(items);
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

    private static ChannelItemResult Result(List<ChannelItemInfo> items)
        => new ChannelItemResult { Items = items, TotalRecordCount = items.Count };

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
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
