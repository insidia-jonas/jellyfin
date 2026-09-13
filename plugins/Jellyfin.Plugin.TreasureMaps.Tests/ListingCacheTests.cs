using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ListingCacheTests
{
    [Fact]
    public async Task GetOrFetch_ServesSnapshotOnlyWhileFresh()
    {
        var now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        var fetches = 0;
        var cache = new TreasureMapsListingCache(() => now, () => "idx-1");
        var first = await cache.GetOrFetchAsync(
            "movies",
            TimeSpan.FromMinutes(5),
            (_, _) =>
            {
                fetches++;
                return Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("a")));
            },
            CancellationToken.None);

        Assert.Equal("a", Assert.Single(first!.Items).Guid);
        Assert.True(cache.TryGetFresh<ReleaseListResponse>("movies", out var fresh, out _));
        Assert.Equal("a", Assert.Single(fresh!.Items).Guid);

        now = now.AddMinutes(6);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("movies", out _, out _));

        var second = await cache.GetOrFetchAsync(
            "movies",
            TimeSpan.FromMinutes(5),
            (_, _) =>
            {
                fetches++;
                return Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("b")));
            },
            CancellationToken.None);

        Assert.Equal(2, fetches);
        Assert.Equal("b", Assert.Single(second!.Items).Guid);
    }

    [Fact]
    public async Task ExpiredOrInvalidated_IsNotReturned()
    {
        var now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        var identity = "idx-1";
        var cache = new TreasureMapsListingCache(() => now, () => identity);
        await cache.GetOrFetchAsync(
            "tv",
            TimeSpan.FromMinutes(5),
            (_, _) => Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("old"))),
            CancellationToken.None);

        now = now.AddMinutes(6);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("tv", out _, out _));

        identity = "idx-2";
        now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("tv", out _, out _));

        cache.InvalidateAll();
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("tv", out _, out _));
        Assert.Equal(1, cache.Generation);
    }

    [Fact]
    public async Task InFlightRequests_AreCoalesced()
    {
        var cache = new TreasureMapsListingCache(() => DateTimeOffset.UtcNow, () => "idx");
        var started = 0;
        var gate = new TaskCompletionSource<ReleaseListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<ListingFetch<ReleaseListResponse>> Fetch(string? _, CancellationToken __)
        {
            Interlocked.Increment(ref started);
            var page = await gate.Task.ConfigureAwait(false);
            return ListingFetch<ReleaseListResponse>.Store(page);
        }

        var first = cache.GetOrFetchAsync("search:matrix", TimeSpan.FromSeconds(20), Fetch, CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref started) == 1);
        var second = cache.GetOrFetchAsync("search:matrix", TimeSpan.FromSeconds(20), Fetch, CancellationToken.None);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref started));

        gate.SetResult(Page("m"));
        var a = await first;
        var b = await second;
        Assert.Equal("m", Assert.Single(a!.Items).Guid);
        Assert.Equal("m", Assert.Single(b!.Items).Guid);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task WarmupThenInvalidate_DoesNotServeStale()
    {
        var now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        var cache = new TreasureMapsListingCache(() => now, () => "idx");
        await cache.GetOrFetchAsync(
            "warmup:movies",
            TreasureMapsListingCache.BrowseFreshTtl,
            (_, _) => Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("warm"))),
            CancellationToken.None);

        Assert.True(cache.TryGetFresh<ReleaseListResponse>("warmup:movies", out _, out _));
        cache.InvalidateAll();
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("warmup:movies", out var stale, out _));
        Assert.Null(stale);

        var fetches = 0;
        var again = await cache.GetOrFetchAsync(
            "warmup:movies",
            TreasureMapsListingCache.BrowseFreshTtl,
            (_, _) =>
            {
                fetches++;
                return Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("fresh")));
            },
            CancellationToken.None);

        Assert.Equal(1, fetches);
        Assert.Equal("fresh", Assert.Single(again!.Items).Guid);
    }

    [Fact]
    public async Task EmptyAndFailedFetches_AreNotCached()
    {
        var fetches = 0;
        var cache = new TreasureMapsListingCache(() => DateTimeOffset.UtcNow, () => "idx");
        var empty = await cache.GetOrFetchAsync(
            "empty",
            TimeSpan.FromMinutes(5),
            (_, _) =>
            {
                fetches++;
                return Task.FromResult(ListingFetch<ReleaseListResponse>.DoNotStore(new ReleaseListResponse()));
            },
            CancellationToken.None);

        Assert.NotNull(empty);
        Assert.Empty(empty!.Items);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("empty", out _, out _));

        await cache.GetOrFetchAsync(
            "empty",
            TimeSpan.FromMinutes(5),
            (_, _) =>
            {
                fetches++;
                return Task.FromResult(ListingFetch<ReleaseListResponse>.DoNotStore(new ReleaseListResponse()));
            },
            CancellationToken.None);
        Assert.Equal(2, fetches);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrFetchAsync<ReleaseListResponse>(
            "err",
            TimeSpan.FromMinutes(5),
            (_, _) => throw new InvalidOperationException("indexer 503"),
            CancellationToken.None));
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("err", out _, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task InFlightGrace_MayServeUntilRefreshCompletes()
    {
        var now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        var cache = new TreasureMapsListingCache(() => now, () => "idx");
        await cache.GetOrFetchAsync(
            "grace",
            TimeSpan.FromMinutes(5),
            (_, _) => Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("live"))),
            CancellationToken.None);

        now = now.AddMinutes(5).AddSeconds(10);
        var gate = new TaskCompletionSource<ReleaseListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cache.GetOrFetchAsync(
            "grace",
            TimeSpan.FromMinutes(5),
            async (_, _) => ListingFetch<ReleaseListResponse>.Store(await gate.Task),
            CancellationToken.None);

        await WaitUntil(() => cache.TryGetFresh<ReleaseListResponse>("grace", out _, out _));
        Assert.True(cache.TryGetFresh<ReleaseListResponse>("grace", out var during, out _));
        Assert.Equal("live", Assert.Single(during!.Items).Guid);

        now = now.AddMinutes(2);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("grace", out _, out _));

        gate.SetResult(Page("newer"));
        Assert.Equal("newer", Assert.Single((await refresh)!.Items).Guid);
    }

    [Fact]
    public async Task NotModified_RevalidatesExpiredSnapshot()
    {
        var now = new DateTimeOffset(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);
        var cache = new TreasureMapsListingCache(() => now, () => "idx");
        await cache.GetOrFetchAsync(
            "etag",
            TimeSpan.FromMinutes(5),
            (_, _) => Task.FromResult(ListingFetch<ReleaseListResponse>.Store(Page("same"), "\"v1\"", "h1")),
            CancellationToken.None);

        now = now.AddMinutes(6);
        Assert.False(cache.TryGetFresh<ReleaseListResponse>("etag", out _, out _));

        var again = await cache.GetOrFetchAsync(
            "etag",
            TimeSpan.FromMinutes(5),
            (validator, _) =>
            {
                Assert.Equal("\"v1\"", validator);
                return Task.FromResult(ListingFetch<ReleaseListResponse>.Validated("\"v1\"", "h1"));
            },
            CancellationToken.None);

        Assert.Equal("same", Assert.Single(again!.Items).Guid);
        Assert.True(cache.TryGetFresh<ReleaseListResponse>("etag", out _, out _));
    }

    [Fact]
    public void CacheTtlForQuery_MatchesFreshnessWindows()
    {
        Assert.Equal(TreasureMapsListingCache.BrowseFreshTtl, TreasureMapsApiClient.CacheTtlForQuery("*"));
        Assert.Equal(TreasureMapsListingCache.LiveSearchFreshTtl, TreasureMapsApiClient.CacheTtlForQuery("matrix"));
    }

    private static ReleaseListResponse Page(string guid)
        => new()
        {
            Items =
            [
                new Release { Guid = guid, Title = "Title." + guid }
            ]
        };

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
