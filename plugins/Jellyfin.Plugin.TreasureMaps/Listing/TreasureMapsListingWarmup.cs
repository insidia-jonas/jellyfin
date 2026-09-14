using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>
/// Prefetches root category / trending indexer pages after server start and after config save
/// so the first Fire TV open is not a cold indexer storm. Uses the same freshness rules as
/// live browse — never stores empty or error results.
/// </summary>
public sealed class TreasureMapsListingWarmupHost : IHostedService
{
    private readonly TreasureMapsApiClient _client;
    private readonly TreasureMapsListingCache _cache;
    private readonly ILogger<TreasureMapsListingWarmupHost> _logger;
    private CancellationTokenSource? _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsListingWarmupHost"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="cache">The listing cache.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsListingWarmupHost(
        TreasureMapsApiClient client,
        TreasureMapsListingCache cache,
        ILogger<TreasureMapsListingWarmupHost> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        RequestWarmup();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        CancelRun();
        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        _cache.InvalidateAll();
        MetadataCatalog.Clear();
        RequestWarmup();
    }

    /// <summary>
    /// Starts a coalesced warmup (in-flight indexer calls share the listing cache).
    /// </summary>
    public void RequestWarmup()
    {
        CancelRun();
        var cts = new CancellationTokenSource();
        _run = cts;
        _ = WarmSafeAsync(cts.Token);
    }

    /// <summary>
    /// Prefetches the common browse queries sequentially so the indexer is not hammered.
    /// </summary>
    /// <param name="client">The API client.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when warmup finishes.</returns>
    public static async Task WarmAsync(TreasureMapsApiClient client, CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return;
        }

        await TryAsync(() => client.GetCapsAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchMoviesAsync(null, null, null, 100, 0, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchMoviesAsync(null, null, null, 100, 100, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchTvAsync(null, null, 100, 0, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchTvAsync(null, null, 100, 100, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchMoviesAsync(null, null, TreasureMapsApiClient.GermanMovieCategories, 100, 0, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.SearchTvAsync(null, TreasureMapsApiClient.GermanTvCategories, 100, 0, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetTrendingAsync("movie", 24, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetTrendingAsync("tv", 24, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetSpotlightAsync("movie", 1, 30, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetSpotlightAsync("movie", 3, 30, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetSpotlightAsync("tv", 1, 30, cancellationToken)).ConfigureAwait(false);
        await TryAsync(() => client.GetSpotlightAsync("tv", 3, 30, cancellationToken)).ConfigureAwait(false);
    }

    private async Task WarmSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            await WarmAsync(_client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Treasure-Maps listing warmup stopped");
        }
    }

    private void CancelRun()
    {
        var previous = Interlocked.Exchange(ref _run, null);
        if (previous is null)
        {
            return;
        }

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        previous.Dispose();
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Warmup must not cache errors; the next live open fetches fresh.
        }
    }
}
