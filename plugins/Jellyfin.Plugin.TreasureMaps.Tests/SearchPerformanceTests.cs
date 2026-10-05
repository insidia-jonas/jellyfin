using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Jellyfin.Plugin.TreasureMaps.Metadata;
using Jellyfin.Plugin.TreasureMaps.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class SearchPerformanceTests
{
    [Fact]
    public async Task ConcurrentCacheMisses_StartOnlyOneFetch()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ListingFetch<string>> Fetch(string? _, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return ListingFetch<string>.Store("result");
        }

        var requests = Enumerable.Range(0, 32).Select(_ => cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), Fetch, CancellationToken.None)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        finish.SetResult();
        Assert.All(await Task.WhenAll(requests), value => Assert.Equal("result", value));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledWaiter_DoesNotCancelOtherWaiters(bool cancelFirst)
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        using var firstToken = new CancellationTokenSource();
        using var secondToken = new CancellationTokenSource();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ListingFetch<string>> Fetch(string? _, CancellationToken token)
        {
            await finish.Task.WaitAsync(token);
            return ListingFetch<string>.Store("result");
        }

        var first = cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), Fetch, firstToken.Token);
        var second = cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), Fetch, secondToken.Token);
        await (cancelFirst ? firstToken : secondToken).CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await (cancelFirst ? first : second).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        finish.SetResult();
        Assert.Equal("result", await (cancelFirst ? second : first));
    }

    [Fact]
    public async Task LastWaiterCancellation_StopsUpstreamWork()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrFetchAsync<string>("key", TimeSpan.FromMinutes(5), async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
            return ListingFetch<string>.Store("unused");
        }, caller.Token);
        await started.Task;
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Invalidation_DiscardsLateResponseEvenIfTransportIgnoresCancellation()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = cache.GetOrFetchAsync<string>("key", TimeSpan.FromMinutes(5), async (_, _) =>
        {
            entered.SetResult();
            await finish.Task;
            return ListingFetch<string>.Store("old");
        }, CancellationToken.None);
        await entered.Task;
        cache.InvalidateAll();
        Assert.Equal("new", await cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), (_, _) => Task.FromResult(ListingFetch<string>.Store("new")), CancellationToken.None));
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await old);
        Assert.True(cache.TryGetFresh<string>("key", out var stored, out _));
        Assert.Equal("new", stored);
    }

    [Fact]
    public async Task SameHash_DoesNotDiscardUpdatedMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new TreasureMapsListingCache(() => now, () => "test");
        await cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), (_, _) => Task.FromResult(ListingFetch<string>.Store("old poster", hash: "same")), CancellationToken.None);
        now = now.AddMinutes(6);
        Assert.Equal("new poster", await cache.GetOrFetchAsync("key", TimeSpan.FromMinutes(5), (_, _) => Task.FromResult(ListingFetch<string>.Store("new poster", hash: "same")), CancellationToken.None));
    }

    [Fact]
    public async Task CapacityEviction_PreservesMostWarmQueries()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        for (var i = 0; i < 257; i++)
            await cache.GetOrFetchAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromMinutes(5), (_, _) => Task.FromResult(ListingFetch<string>.Store("warm")), CancellationToken.None);
        Assert.InRange(cache.Count, 200, 256);
    }

    [Fact]
    public async Task RequestGate_BoundsConcurrencyAndCancelsQueuedRequests()
    {
        using var gate = new IndexerRequestGate();
        using var a = await gate.EnterAsync(CancellationToken.None);
        using var b = await gate.EnterAsync(CancellationToken.None);
        using var c = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var queued = gate.EnterAsync(cancellation.Token);
        Assert.False(queued.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        a.Dispose();
        using var next = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RequestGate_RespectsCooldownAndRecovers()
    {
        var clock = new TestClock();
        using var gate = new IndexerRequestGate(clock);
        gate.BackOff(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () => await gate.EnterAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        clock.Now = clock.Now.AddSeconds(6);
        using var next = await gate.EnterAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Search_OnlyLoadsSecondPageWhenFirstPageCannotFillCards()
    {
        var calls = new List<(string, int)>();
        var results = await LiveSearchPages.FetchAsync("Matrix", 24, (kind, offset, _) =>
        {
            calls.Add((kind, offset));
            IReadOnlyList<Release> page = kind == "movie" ? Enumerable.Range(0, 30).Select(i => new Release { Guid = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Title = "Matrix " + i, Movie = new ReleaseMovie { Title = "Matrix " + i } }).ToArray() : [];
            return Task.FromResult((page, true));
        }, CancellationToken.None);
        Assert.Equal(30, results.Count);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal(0, call.Item2));
    }

    [Fact]
    public async Task Search_ContinuesOnlyFullSuccessfulPages()
    {
        var calls = new List<(string, int)>();
        await LiveSearchPages.FetchAsync("Matrix", 24, (kind, offset, _) =>
        {
            calls.Add((kind, offset));
            IReadOnlyList<Release> page = kind == "movie" && offset == 0
                ? Enumerable.Range(0, 100).Select(i => new Release { Guid = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Title = "Matrix", Movie = new ReleaseMovie { Title = "Matrix" } }).ToArray() : [];
            return Task.FromResult((page, true));
        }, CancellationToken.None);
        Assert.Equal(new[] { ("movie", 0), ("tv", 0), ("movie", 100) }, calls);
    }

    [Fact]
    public async Task Search_DeadlinePreservesResultsFromFasterProvider()
    {
        var results = await LiveSearchPages.FetchAsync("Matrix", 24, async (kind, _, token) =>
        {
            if (kind == "tv") await Task.Delay(Timeout.Infinite, token);
            return ((IReadOnlyList<Release>)[new Release { Guid = "matrix", Title = "Matrix" }], true);
        }, CancellationToken.None, TimeSpan.FromMilliseconds(100));
        Assert.Equal("matrix", Assert.Single(results).Guid);
    }

    [Fact]
    public async Task Search_AllFailedPagesAreNotReportedAsSuccessfulEmptySearch()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => LiveSearchPages.FetchAsync("Matrix", 24,
            (_, _, _) => Task.FromResult(((IReadOnlyList<Release>)Array.Empty<Release>(), false)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveSearchPages.FetchAsync("Matrix", 24, async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ((IReadOnlyList<Release>)Array.Empty<Release>(), true);
        }, cancellation.Token));
    }

    [Fact]
    public void MatchingYear_DoesNotMakeUnrelatedTitleRelevant()
        => Assert.Equal(0, TreasureMapsSearch.ScoreTitle("Completely unrelated", "Matrix 1999", 1999));

    [Fact]
    public async Task Typeahead_UsesProviderArtworkWithoutMetadataRequests()
    {
        using var handler = new MetadataHandler();
        var catalog = new MetadataCatalog(new ClientFactory(handler), NullLogger<MetadataCatalog>.Instance);
        var group = new ReleaseGroup { Title = "Cached only test", Imdb = "tt0133093" };
        await catalog.FillAsync([group], CancellationToken.None, allowNetwork: false);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("0133093", group.Cover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_BoundsConcurrentRequestsAndCoalescesTitles()
    {
        using var handler = new MetadataHandler();
        var catalog = new MetadataCatalog(new ClientFactory(handler), NullLogger<MetadataCatalog>.Instance);
        var name = Guid.NewGuid().ToString();
        var groups = Enumerable.Range(0, 20).Select(_ => new ReleaseGroup { Title = name }).ToArray();
        await catalog.FillAsync(groups, CancellationToken.None);
        await catalog.GetAsync(groups[0], CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        await catalog.FillAsync([new ReleaseGroup { Title = name }], CancellationToken.None);
        Assert.Equal(1, handler.Calls); // Negative results also have a finite cache lifetime.
        var distinct = Enumerable.Range(0, 20).Select(i => new ReleaseGroup { Title = name + i }).ToArray();
        await catalog.FillAsync(distinct, CancellationToken.None);
        await Task.WhenAll(distinct.Select(g => catalog.GetAsync(g, CancellationToken.None)));
        Assert.InRange(handler.Maximum, 1, 3);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public int Calls;
        public int Maximum;
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref _active);
            lock (this) Maximum = Math.Max(Maximum, active);
            try { await Task.Delay(20, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") };
        }
    }
}
