using Jellyfin.LiveTv.Channels;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class LiveTvLibraryChannelPresentationTests
{
    [Theory]
    [InlineData("DE | Das Erste HD", "Das Erste HD")]
    [InlineData("DE - ZDF", "ZDF")]
    [InlineData("  CNN   International  ", "CNN International")]
    [InlineData(null, "")]
    public void CleanName_StripsPlaylistJunk(string? raw, string expected)
    {
        Assert.Equal(expected, LiveTvLibraryChannelPresentation.CleanName(raw));
    }

    [Fact]
    public void CardName_AppendsNowPlaying()
    {
        var name = LiveTvLibraryChannelPresentation.CardName(
            "Das Erste",
            new LiveTvNowNext { NowTitle = "Tagesschau" });

        Assert.Equal("Das Erste  ·  Tagesschau", name);
    }

    [Fact]
    public void Overview_IsEmptyWithoutGuide()
    {
        Assert.Equal(string.Empty, LiveTvLibraryChannelPresentation.Overview(null));
    }
}
