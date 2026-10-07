#pragma warning disable RS0030 // Do not use banned APIs
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Library.Search;

/// <summary>
/// Built-in SQL-based search provider that queries the library database directly.
/// </summary>
public class SqlSearchProvider : IInternalSearchProvider
{
    private const int DefaultSearchLimit = 100;

    private static readonly Guid _placeholderId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemQueryHelpers _queryHelpers;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlSearchProvider"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="itemTypeLookup">The item type lookup.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="queryHelpers">The shared item query helpers.</param>
    public SqlSearchProvider(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IItemTypeLookup itemTypeLookup,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IItemQueryHelpers queryHelpers)
    {
        _dbProvider = dbProvider;
        _itemTypeLookup = itemTypeLookup;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queryHelpers = queryHelpers;
    }

    /// <inheritdoc/>
    public string Name => "Database";

    /// <inheritdoc/>
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc/>
    public int Priority => 100; // Low priority - runs as fallback

    /// <inheritdoc/>
    public bool CanSearch(SearchProviderQuery query)
    {
        // SQL search can always handle any query
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.SearchTerm);

        var parsed = MediaSearch.Parse(query.SearchTerm);
        var words = MediaSearch.Keywords(parsed.AlternateText ?? parsed.Text);
        if (words.Length == 0 && parsed.ImdbId is null)
        {
            return [];
        }

        var limit = Math.Clamp(query.Limit ?? DefaultSearchLimit, 1, 200);
        await using var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var eligible = dbContext.BaseItems.AsNoTracking().Where(e => e.Id != _placeholderId && !e.IsVirtualItem);
        eligible = ApplyTypeFilter(eligible, query.IncludeItemTypes, query.ExcludeItemTypes);
        eligible = ApplyMediaTypeFilter(eligible, query.MediaTypes);
        eligible = ApplyParentFilter(eligible, query.ParentId);
        eligible = ApplyUserAccessFilter(dbContext, eligible, query);
        if (!query.ParentId.HasValue || query.ParentId.Value.IsEmpty())
        {
            // Cached indexer title cards are query-specific snapshots. Fresh external search owns
            // them; otherwise every previous spelling contributes a duplicate or a stale result.
            var boxSets = MapKindsToTypeNames([BaseItemKind.BoxSet]);
            eligible = eligible.Where(e => e.ChannelId == null || e.ChannelId == Guid.Empty
                || (!boxSets.Contains(e.Type) && !e.Provider!.Any(p => p.ProviderId == "TreasureMapsKind")));
        }

        if (parsed.Kind == "movie")
        {
            eligible = eligible.Where(e => e.IsMovie);
        }
        else if (parsed.Kind == "tv")
        {
            eligible = eligible.Where(e => e.IsSeries || e.SeriesId != null);
        }

        if (parsed.Year.HasValue)
        {
            eligible = eligible.Where(e => e.ProductionYear == parsed.Year);
        }

        if (parsed.ImdbId is not null)
        {
            eligible = eligible.Where(e => e.Provider!.Any(p => p.ProviderId == "Imdb" && p.ProviderValue == parsed.ImdbId));
        }

        if (parsed.Season.HasValue)
        {
            eligible = eligible.Where(e => (e.ParentIndexNumber == parsed.Season && (!parsed.Episode.HasValue || e.IndexNumber == parsed.Episode))
                || (e.IsSeries && dbContext.BaseItems.Any(child => child.SeriesId == e.Id && child.ParentIndexNumber == parsed.Season
                    && (!parsed.Episode.HasValue || child.IndexNumber == parsed.Episode))));
        }

        var strict = eligible;
        foreach (var word in words)
        {
            var like = "%" + word + "%";
            strict = strict.Where(e => e.CleanName!.Contains(word)
                || EF.Functions.Like(e.OriginalTitle!, like) || EF.Functions.Like(e.SeriesName!, like)
                || EF.Functions.Like(e.Overview!, like) || EF.Functions.Like(e.Genres!, like)
                || e.Peoples!.Any(p => EF.Functions.Like(p.People.Name!, like)));
        }

        var ranked = await RankAsync(strict).ConfigureAwait(false);
        if (ranked.Count == 0 && !parsed.Exact && parsed.ImdbId is null && words.Any(w => w.Length >= 4))
        {
            // Retrieve a bounded set of title candidates, then verify every word with the shared
            // typo matcher. Descriptive text is never fuzzily expanded into unrelated results.
            var fuzzy = eligible;
            foreach (var word in words)
            {
                var prefix = "%" + (word.Length >= 4 ? word[..2] : word) + "%";
                var suffix = "%" + (word.Length >= 4 ? word[^2..] : word) + "%";
                var edges = word.Length >= 4 ? "%" + word[0] + "%" + word[^1] + "%" : prefix;
                fuzzy = fuzzy.Where(e => EF.Functions.Like(e.CleanName!, prefix) || EF.Functions.Like(e.CleanName!, suffix) || EF.Functions.Like(e.CleanName!, edges)
                    || EF.Functions.Like(e.OriginalTitle!, prefix) || EF.Functions.Like(e.OriginalTitle!, suffix) || EF.Functions.Like(e.OriginalTitle!, edges)
                    || EF.Functions.Like(e.SeriesName!, prefix) || EF.Functions.Like(e.SeriesName!, suffix) || EF.Functions.Like(e.SeriesName!, edges));
            }

            ranked = await RankAsync(fuzzy).ConfigureAwait(false);
        }

        return ranked.OrderByDescending(r => r.Score).ThenBy(r => r.ItemId).Take(limit).ToArray();

        async Task<List<SearchResult>> RankAsync(IQueryable<BaseItemEntity> candidates)
        {
            var name = MediaSearch.Normalize(parsed.AlternateText ?? parsed.Text);
            var rows = await candidates.OrderByDescending(e => e.CleanName == name)
                .ThenByDescending(e => e.CleanName!.StartsWith(name)).ThenBy(e => e.Id).Take(512)
                .Select(e => new
                {
                    e.Id,
                    e.Name,
                    e.OriginalTitle,
                    e.SeriesName,
                    e.Overview,
                    e.Genres,
                    People = e.Peoples!.Select(p => p.People.Name).ToArray()
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(e => new SearchResult(e.Id, parsed.ImdbId is not null ? 100
                    : MediaSearch.ScoreDocument(parsed, [e.Name, e.OriginalTitle, e.SeriesName], e.Overview, e.Genres, string.Join(' ', e.People)) + 5))
                .Where(r => r.Score > 5).ToList();
        }
    }

    private IQueryable<BaseItemEntity> ApplyTypeFilter(
        IQueryable<BaseItemEntity> query,
        BaseItemKind[] includeItemTypes,
        BaseItemKind[] excludeItemTypes)
    {
        if (includeItemTypes.Length > 0)
        {
            var includeTypeNames = MapKindsToTypeNames(includeItemTypes);
            if (includeTypeNames.Count > 0)
            {
                query = query.Where(e => includeTypeNames.Contains(e.Type));
            }
        }
        else if (excludeItemTypes.Length > 0)
        {
            var excludeTypeNames = MapKindsToTypeNames(excludeItemTypes);
            if (excludeTypeNames.Count > 0)
            {
                query = query.Where(e => !excludeTypeNames.Contains(e.Type));
            }
        }

        return query;
    }

    private static IQueryable<BaseItemEntity> ApplyMediaTypeFilter(
        IQueryable<BaseItemEntity> query,
        MediaType[] mediaTypes)
    {
        if (mediaTypes.Length == 0)
        {
            return query;
        }

        var mediaTypeNames = mediaTypes.Select(m => m.ToString()).ToArray();
        return query.Where(e => e.MediaType != null && mediaTypeNames.Contains(e.MediaType));
    }

    private static IQueryable<BaseItemEntity> ApplyParentFilter(
        IQueryable<BaseItemEntity> query,
        Guid? parentId)
    {
        if (!parentId.HasValue || parentId.Value.IsEmpty())
        {
            return query;
        }

        var pid = parentId.Value;
        return query.Where(e => e.ParentId == pid || e.Parents!.Any(p => p.ParentItemId == pid));
    }

    private IQueryable<BaseItemEntity> ApplyUserAccessFilter(
        JellyfinDbContext dbContext,
        IQueryable<BaseItemEntity> query,
        SearchProviderQuery searchQuery)
    {
        var userId = searchQuery.UserId;
        if (!userId.HasValue || userId.Value.IsEmpty())
        {
            return query;
        }

        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return query;
        }

        var accessFilter = SearchQueryAccessFilter.Build(user, searchQuery, _libraryManager);
        return _queryHelpers.ApplyAccessFiltering(dbContext, query, accessFilter);
    }

    private List<string> MapKindsToTypeNames(BaseItemKind[] kinds)
    {
        var list = new List<string>(kinds.Length);
        foreach (var kind in kinds)
        {
            if (_itemTypeLookup.BaseItemKindNames.TryGetValue(kind, out var name) && name is not null)
            {
                list.Add(name);
            }
        }

        return list;
    }
}
