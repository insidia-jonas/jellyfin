using Jellyfin.LiveTv.Listings;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Listings;

public class EpgChannelDataTests
{
    [Fact]
    public void GetChannelByName_MatchesTvgNameWithUnderscores()
    {
        var data = new EpgChannelData(
        [
            new ChannelInfo { Id = "xml-1", Name = "Das Erste" }
        ]);

        Assert.Same(data.GetChannelById("xml-1"), data.GetChannelByName("Das_Erste"));
        Assert.Same(data.GetChannelById("xml-1"), data.GetChannelByName("Das Erste HD"));
    }

    [Fact]
    public void GetChannelById_IndexesTvgName()
    {
        var data = new EpgChannelData(
        [
            new ChannelInfo { Id = "xml-1", Name = "Channel X", TvgName = "Channel_X" }
        ]);

        Assert.Equal("xml-1", data.GetChannelById("Channel_X")?.Id);
    }
}
