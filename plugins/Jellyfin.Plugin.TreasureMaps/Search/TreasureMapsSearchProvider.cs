using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Search;

/// <summary>
/// External search provider so Fire TV / iOS / web Search/Hints query Treasure-Maps live.
/// </summary>
public class TreasureMapsSearchProvider : IExternalSearchProvider
{
    private readonly IChannelManager _channelManager;
    private readonly ILogger<TreasureMapsSearchProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsSearchProvider"/> class.
    /// </summary>
    /// <param name="channelManager">The channel manager (materializes title cards).</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsSearchProvider(IChannelManager channelManager, ILogger<TreasureMapsSearchProvider> logger)
    {
        _channelManager = channelManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Treasure-Maps";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query)
        => TreasureMapsApiClient.IsConfigured && TreasureMapsSearch.IsLiveTitleQuery(query);

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var term = query.SearchTerm.Trim();
        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> items;
        try
        {
            items = await _channelManager.SearchChannelItemsAsync(term, query.UserId, query.Limit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps search failed for '{Term}'", term);
            yield break;
        }

        foreach (var item in items)
        {
            if (item is null || item.Id == Guid.Empty)
            {
                continue;
            }

            var score = TreasureMapsSearch.ScoreTitle(item.Name, term);
            if (score <= 0f)
            {
                score = TreasureMapsSearch.ContainsMatchScore;
            }

            yield return new SearchResult(item.Id, score);
        }
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await foreach (var result in SearchAsync(query, cancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }
}
