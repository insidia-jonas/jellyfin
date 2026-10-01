using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class ChannelSearchCacheTests
{
    [Fact]
    public async Task ConcurrentRequests_MaterializeOnce()
    {
        using var cache = new ChannelSearchCache();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<IReadOnlyList<BaseItem>> Fetch(CancellationToken token)
        {
            calls++;
            await finish.Task.WaitAsync(token);
            return [new Folder { Id = Guid.NewGuid() }];
        }

        var first = cache.GetOrCreateAsync("same-user-version-query", Fetch, TestContext.Current.CancellationToken);
        var second = cache.GetOrCreateAsync("same-user-version-query", Fetch, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        finish.SetResult();
        Assert.Same(await first, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledQueuedRequest_DoesNotAbortActiveRequest()
    {
        using var cache = new ChannelSearchCache();
        using var cancellation = new CancellationTokenSource();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetOrCreateAsync(
            "key",
            async token =>
        {
            await finish.Task.WaitAsync(token);
            return [new Folder()];
        },
            TestContext.Current.CancellationToken);
        var queued = cache.GetOrCreateAsync("key", _ => throw new InvalidOperationException(), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        finish.SetResult();
        Assert.Single(await first);
    }

    [Fact]
    public async Task FailureAndEmptyResults_AreRetried()
    {
        using var cache = new ChannelSearchCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("key", _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Empty(await cache.GetOrCreateAsync("key", _ => Task.FromResult<IReadOnlyList<BaseItem>>([]), TestContext.Current.CancellationToken));
        Assert.Single(await cache.GetOrCreateAsync("key", _ => Task.FromResult<IReadOnlyList<BaseItem>>([new Folder()]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledTransportResponse_IsNotCached()
    {
        using var cache = new ChannelSearchCache();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(
            "key",
            async _ =>
        {
            await cancellation.CancelAsync();
            return [new Folder()];
        },
            cancellation.Token));
        Assert.Empty(await cache.GetOrCreateAsync("key", _ => Task.FromResult<IReadOnlyList<BaseItem>>([]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DifferentScopes_DoNotReuseResults()
    {
        using var cache = new ChannelSearchCache();
        var first = await cache.GetOrCreateAsync("provider-v1-userA-query", _ => Task.FromResult<IReadOnlyList<BaseItem>>([new Folder()]), TestContext.Current.CancellationToken);
        var second = await cache.GetOrCreateAsync("provider-v1-userB-query", _ => Task.FromResult<IReadOnlyList<BaseItem>>([new Folder()]), TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
    }
}
