using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
/// Exposes a Treasure-Maps indexer as a browsable Jellyfin channel that mirrors the website:
/// Trending (Movies / TV Shows), browse Movies / TV Shows, browse by genre and a Find A–Z search.
/// Titles are shown once (one poster card per movie/show); opening a card lists the individual
/// releases (qualities) behind it, which you grab by marking a release as a favorite (heart).
/// </summary>
public class TreasureMapsChannel : IChannel, ISupportsLatestMedia, IDisableMediaSourceDisplay
{
    private const string GenrePrefix = "genre:";
    private const string FindPrefix = "find:";
    private const string GroupPrefix = "GRP::";
    private const string ReleasePrefix = "REL::";
    private const string GrabPrefix = "grab::";
    private const string Sep = "::";
    private const int BrowseFetchLimit = 80;

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
                "14",
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
            var folderId = query.FolderId ?? string.Empty;

            if (string.IsNullOrEmpty(folderId))
            {
                return GetRootFolders();
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
                return await GetTrendingGroupsAsync("movie", cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "trending-tv", StringComparison.Ordinal))
            {
                return await GetTrendingGroupsAsync("tv", cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "movies", StringComparison.Ordinal))
            {
                var movies = await _client.SearchMoviesAsync(null, null, BrowseFetchLimit, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(movies?.Items);
            }

            if (string.Equals(folderId, "tv", StringComparison.Ordinal))
            {
                var tv = await _client.SearchTvAsync(null, BrowseFetchLimit, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(tv?.Items);
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
                var byGenre = await _client.SearchMoviesAsync(null, folderId[GenrePrefix.Length..], BrowseFetchLimit, cancellationToken).ConfigureAwait(false);
                return BuildGroupCards(byGenre?.Items);
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
            Folder("genres", "Browse by genre"),
            Folder("find", "Find A\u2013Z")
        };

        return Result(items);
    }

    private static ChannelItemResult TrendingSubFolders()
        => Result(new List<ChannelItemInfo> { Folder("trending-movie", "Movies"), Folder("trending-tv", "TV Shows") });

    private static ChannelItemResult GetLetterFolders()
    {
        var items = new List<ChannelItemInfo> { Folder(FindPrefix + "0-9", "0-9") };
        for (var c = 'A'; c <= 'Z'; c++)
        {
            items.Add(Folder(FindPrefix + c, c.ToString()));
        }

        return Result(items);
    }

    private async Task<ChannelItemResult> SearchByLetterAsync(string token, CancellationToken cancellationToken)
    {
        // Live search against the indexer, then keep only titles whose (article-stripped) name
        // starts with the chosen letter/digit — a TV-friendly way to look up a specific title.
        var q = string.Equals(token, "0-9", StringComparison.Ordinal) ? null : token;
        var moviesTask = _client.SearchMoviesAsync(q, null, BrowseFetchLimit, cancellationToken);
        var tvTask = _client.SearchTvAsync(q, BrowseFetchLimit, cancellationToken);
        var both = await Task.WhenAll(moviesTask, tvTask).ConfigureAwait(false);

        var releases = (both[0]?.Items ?? Array.Empty<Release>())
            .Concat(both[1]?.Items ?? Array.Empty<Release>())
            .Where(r => StartsWithToken(ResolveTitle(r), token))
            .ToList();

        return BuildGroupCards(releases);
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
    /// Builds one poster card per movie/show (grouping the releases behind it).
    /// </summary>
    private ChannelItemResult BuildGroupCards(IReadOnlyList<Release>? releases)
    {
        var prefs = GetLanguagePreferences();
        var marker = ShortHash(DataVersion);

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
            var card = new ChannelItemInfo
            {
                Id = string.Join(Sep, GroupPrefix.TrimEnd(':'), marker, group.Kind, Encode(group.Key), Encode(group.Title)),
                Name = group.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = group.Cover,
                ProductionYear = group.Year,
                CommunityRating = group.Rating.HasValue ? (float)group.Rating.Value : null,
                Overview = count + (count == 1 ? " release available." : " releases available.")
                    + " Open to choose a quality, then mark it as a favorite (\u2764) to download."
            };

            if (group.Genres.Count > 0)
            {
                card.Genres = group.Genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
        var parts = groupId.Split(Sep);
        if (parts.Length < 5)
        {
            return new ChannelItemResult();
        }

        var kind = parts[2];
        var key = Decode(parts[3]);
        var title = Decode(parts[4]);

        var response = string.Equals(kind, "tv", StringComparison.Ordinal)
            ? await _client.SearchTvAsync(title, BrowseFetchLimit, cancellationToken).ConfigureAwait(false)
            : await _client.SearchMoviesAsync(title, null, BrowseFetchLimit, cancellationToken).ConfigureAwait(false);

        var all = response?.Items ?? Array.Empty<Release>();
        var matching = all.Where(r => string.Equals(ReleaseGrouper.KeyOf(r), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
        {
            matching = all.ToList();
        }

        return await BuildReleaseTilesAsync(matching, groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the individual release tiles (one per release/quality) shown inside a title card.
    /// </summary>
    private async Task<ChannelItemResult> BuildReleaseTilesAsync(IReadOnlyList<Release> releases, string scope, CancellationToken cancellationToken)
    {
        var prefs = GetLanguagePreferences();
        var xrelRatings = await FetchXrelRatingsAsync(releases, cancellationToken).ConfigureAwait(false);
        var marker = ShortHash(DataVersion);
        var scopeHash = ShortHash(scope);

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
                var kind = item.ProviderIds.TryGetValue("TreasureMapsKind", out var k) && !string.IsNullOrEmpty(k) ? k : "movie";
                // Inside a title card, show the distinguishing release/scene name (quality, source,
                // language, group) rather than the movie title so the qualities are told apart.
                if (!string.IsNullOrWhiteSpace(release.Title))
                {
                    item.Name = release.Title;
                }

                // REL::<scopeHash>::<marker>::<kind>::<guid> — unique per title card, refreshed on config change.
                item.Id = string.Join(Sep, ReleasePrefix.TrimEnd(':'), scopeHash, marker, kind, item.Id);
                ranked.Add((item, rank, order++));
            }
        }

        var items = ranked
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Order)
            .Select(x => x.Item)
            .Take(Config.ResultLimit)
            .ToList();

        return Result(items);
    }

    private static ChannelItemResult GetReleaseDetail(string folderId)
    {
        // REL::<scopeHash>::<marker>::<kind>::<guid>
        var parts = folderId.Split(Sep);
        var guid = parts[^1];
        var kind = parts.Length >= 2 ? parts[^2] : "movie";
        if (!string.Equals(kind, "tv", StringComparison.Ordinal))
        {
            kind = "movie";
        }

        var child = new ChannelItemInfo
        {
            Id = GrabPrefix.TrimEnd(':') + Sep + guid,
            Name = "\u2193 Download \u2013 mark as favorite (\u2764)",
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            Overview = "Mark this entry as a favorite (the \u2764 icon) to send the release to your download client (SABnzbd)."
        };
        child.ProviderIds["TreasureMaps"] = guid;
        child.ProviderIds["TreasureMapsKind"] = kind;

        return Result(new List<ChannelItemInfo> { child });
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
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Array.Empty<ChannelItemInfo>();
        }

        try
        {
            var movies = await _client.SearchMoviesAsync(null, null, BrowseFetchLimit, cancellationToken).ConfigureAwait(false);
            return BuildGroupCards(movies?.Items).Items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load latest Treasure-Maps media");
            return Array.Empty<ChannelItemInfo>();
        }
    }

    /// <summary>
    /// Builds the Trending Movies / Trending TV rows. The /trending feed only carries an imdb id
    /// (no covers), so each item is enriched via a title lookup to pull the poster + metadata.
    /// </summary>
    private async Task<ChannelItemResult> GetTrendingGroupsAsync(string kind, CancellationToken cancellationToken)
    {
        var response = await _client.GetTrendingAsync(kind, 15, cancellationToken).ConfigureAwait(false);
        var items = response?.Items ?? Array.Empty<Release>();

        var enriched = await Task.WhenAll(items.Select(it => EnrichTrendingAsync(it, kind, cancellationToken))).ConfigureAwait(false);
        var releases = enriched.Where(r => r is not null).Select(r => r!).ToList();
        return BuildGroupCards(releases);
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
        var caps = await _client.GetCapsAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>();
        if (caps?.Genres is not null)
        {
            foreach (var genre in caps.Genres.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
            {
                items.Add(Folder(GenrePrefix + genre.Name, genre.Name!));
            }
        }

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
