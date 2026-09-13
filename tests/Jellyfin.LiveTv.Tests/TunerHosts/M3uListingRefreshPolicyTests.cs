using System;
using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uListingRefreshPolicyTests
{
    private const string Playlist = "https://cdn.example/playlist.m3u";

    [Fact]
    public void ShouldRefreshListing_MissingSnapshot_IsTrue()
    {
        Assert.True(M3uListingRefreshPolicy.ShouldRefreshListing(null, DateTime.UtcNow, null, Playlist));
    }

    [Fact]
    public void ShouldRefreshListing_WithinInterval_IsFalse()
    {
        var fetched = DateTime.UtcNow.AddMinutes(-10);
        Assert.False(M3uListingRefreshPolicy.ShouldRefreshListing(fetched, DateTime.UtcNow, Playlist, Playlist));
    }

    [Fact]
    public void ShouldRefreshListing_AfterInterval_IsTrue()
    {
        var fetched = DateTime.UtcNow.AddMinutes(-46);
        Assert.True(M3uListingRefreshPolicy.ShouldRefreshListing(fetched, DateTime.UtcNow, Playlist, Playlist));
    }

    [Fact]
    public void ShouldRefreshListing_UrlChanged_IsTrue()
    {
        var fetched = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(M3uListingRefreshPolicy.ShouldRefreshListing(
            fetched,
            DateTime.UtcNow,
            Playlist,
            "https://cdn.example/other.m3u"));
    }

    [Fact]
    public void ShouldSendConditionalGet_WhenValidatorsPresent()
    {
        Assert.False(M3uListingRefreshPolicy.ShouldSendConditionalGet(null, null));
        Assert.True(M3uListingRefreshPolicy.ShouldSendConditionalGet("\"abc\"", null));
        Assert.True(M3uListingRefreshPolicy.ShouldSendConditionalGet(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ChannelSetIdentity_IgnoresOrderAndNowNext()
    {
        var first = M3uListingRefreshPolicy.ChannelSetIdentity(["b", "a"]);
        var second = M3uListingRefreshPolicy.ChannelSetIdentity(["a", "b"]);
        var third = M3uListingRefreshPolicy.ChannelSetIdentity(["a", "b", "c"]);

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.False(string.IsNullOrEmpty(first));
    }

    [Fact]
    public void ListingRefreshInterval_IsConservative()
    {
        Assert.Equal(TimeSpan.FromMinutes(45), M3uListingRefreshPolicy.ListingRefreshInterval);
        Assert.InRange(M3uListingRefreshPolicy.ListingRefreshInterval.TotalMinutes, 30, 60);
    }
}
