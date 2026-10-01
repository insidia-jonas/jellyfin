using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Listing;
using MediaBrowser.Controller.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class FolderListingTests
{
    [Theory]
    [InlineData("movie", "Sut.Kardesler.1976.1080p.WEB-DL", "The Foster Brothers", "Sut Kardesler")]
    [InlineData("tv", "Haus.des.Geldes.S01E02.German.1080p", "Money Heist", "Haus des Geldes")]
    public void TitleCard_KeepsTheSearchableReleaseName(string kind, string scene, string displayTitle, string query)
    {
        var group = new Jellyfin.Plugin.TreasureMaps.Channels.ReleaseGroup { Kind = kind, Title = displayTitle };
        group.Releases.Add(new Jellyfin.Plugin.TreasureMaps.Api.Release { Guid = "release", Title = scene });
        Assert.Equal(query, Jellyfin.Plugin.TreasureMaps.Channels.ReleaseGrouper.SearchTitleOf(group));
        Assert.Equal(displayTitle, group.Title);
    }

    [Fact]
    public async Task ColdFolder_WaitsForCardsAndSharesFetch()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        var completion = new TaskCompletionSource<ChannelItemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<ChannelItemResult> Fetch(CancellationToken _) { Interlocked.Increment(ref calls); return completion.Task; }
        var first = TreasureMapsFolderListing.LoadAsync(cache, "folder", TimeSpan.FromMinutes(5), Fetch, TestContext.Current.CancellationToken);
        var second = TreasureMapsFolderListing.LoadAsync(cache, "folder", TimeSpan.FromMinutes(5), Fetch, TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        completion.SetResult(new ChannelItemResult { Items = [new ChannelItemInfo { Id = "movie", Name = "Movie" }] });
        Assert.Equal("movie", Assert.Single((await first).Items).Id);
        Assert.Same(await first, await second);
        Assert.Equal(1, calls);
        Assert.Same(await first, await TreasureMapsFolderListing.LoadAsync(cache, "folder", TimeSpan.FromMinutes(5), Fetch, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PendingOrEmptyBuild_IsNotAValidEmptyFolder()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        foreach (var listing in new[] { ChannelItemResult.Pending(), new ChannelItemResult() })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => TreasureMapsFolderListing.LoadAsync(
                cache, "folder", TimeSpan.FromMinutes(5), _ => Task.FromResult(listing), TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task LeavingFolder_CancelsOnlyThatViewer()
    {
        var cache = new TreasureMapsListingCache(identity: () => "test");
        using var leaving = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ChannelItemResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ChannelItemResult> Fetch(CancellationToken token) => completion.Task.WaitAsync(token);
        var first = TreasureMapsFolderListing.LoadAsync(cache, "folder", TimeSpan.FromMinutes(5), Fetch, leaving.Token);
        var second = TreasureMapsFolderListing.LoadAsync(cache, "folder", TimeSpan.FromMinutes(5), Fetch, TestContext.Current.CancellationToken);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        completion.SetResult(new ChannelItemResult { Items = [new ChannelItemInfo { Id = "movie" }] });
        Assert.Single((await second).Items);
    }
}
