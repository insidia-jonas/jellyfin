using System;
using Jellyfin.LiveTv.Health;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class IptvSourceChoiceTests
{
    [Fact]
    public void SelectionIsScopedToChannelAndAccessAndNeverAcceptsArbitraryOrigins()
    {
        var tuner = Tuner();
        var selected = IptvSourceChoice.Id(tuner, "channel-a", "https://backup.example");
        Assert.Equal("https://backup.example", IptvSourceChoice.Resolve(tuner, "channel-a", selected));
        Assert.Null(IptvSourceChoice.Resolve(tuner, "channel-b", selected));
        Assert.Null(IptvSourceChoice.Resolve(tuner, "channel-a", "https://evil.example"));
        tuner.Url += "?new-access";
        Assert.Null(IptvSourceChoice.Resolve(tuner, "channel-a", selected));
    }

    [Fact]
    public void SelectionClonesSourceAndOnlyChangesItsOpenTokenAndOrigin()
    {
        var tuner = Tuner();
        var source = new MediaSourceInfo
        {
            Id = "channel-a",
            Path = "https://primary.example/private/stream.ts?key=secret",
            OpenToken = "provider_LiveTvChannel_item_channel-a",
            RequiresOpening = true,
            IsInfiniteStream = true
        };
        var selectedId = IptvSourceChoice.Id(tuner, source.Id, "https://backup.example");
        var selected = IptvSourceSelector.SelectSource(tuner, source.Id, [source], selectedId);
        Assert.NotNull(selected);
        Assert.Equal("https://backup.example/private/stream.ts?key=secret", selected.Path);
        Assert.Equal("provider_LiveTvChannel_item_" + selectedId, selected.OpenToken);
        Assert.Equal(selectedId, selected.Id);
        Assert.Equal("channel-a", source.Id);
        Assert.Equal("provider_LiveTvChannel_item_channel-a", source.OpenToken);
        Assert.Equal("https://primary.example", tuner.ActiveUrl);
        Assert.Null(IptvSourceSelector.SelectSource(tuner, "other", [source], selectedId));
        source.RequiresOpening = false;
        Assert.Null(IptvSourceSelector.SelectSource(tuner, "channel-a", [source], selectedId));
    }

    [Fact]
    public void RemovedSourceInvalidatesPreviouslyIssuedSelection()
    {
        var tuner = Tuner();
        var selected = IptvSourceChoice.Id(tuner, "channel", "https://backup.example");
        tuner.AlternateUrls = ["https://primary.example"];
        Assert.Null(IptvSourceChoice.Resolve(tuner, "channel", selected));
        Assert.DoesNotContain("secret", selected, StringComparison.Ordinal);
    }

    private static TunerHostInfo Tuner() => new()
    {
        Id = "tuner",
        Type = "m3u",
        Url = "https://listing.example/private.m3u",
        ActiveUrl = "https://primary.example",
        AlternateUrls = ["https://primary.example", "https://backup.example"]
    };
}
