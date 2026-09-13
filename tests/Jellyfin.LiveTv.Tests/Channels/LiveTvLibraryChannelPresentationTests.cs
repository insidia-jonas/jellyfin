using System;
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
    public void CardName_IsSenderOnly()
    {
        var name = LiveTvLibraryChannelPresentation.CardName(
            "Das Erste",
            new LiveTvNowNext { NowTitle = "Tagesschau" });

        Assert.Equal("Das Erste", name);
        Assert.DoesNotContain("Tagesschau", name, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramSubtitle_SeparatesNowAndNextWithTimes()
    {
        var start = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 13, 18, 15, 0, DateTimeKind.Utc);
        var nextEnd = new DateTime(2026, 9, 13, 19, 0, 0, DateTimeKind.Utc);
        var subtitle = LiveTvLibraryChannelPresentation.ProgramSubtitle(
            new LiveTvNowNext
            {
                NowTitle = "Tagesschau",
                NowStart = start,
                NowEnd = end,
                NextTitle = "Wetter",
                NextStart = end,
                NextEnd = nextEnd
            });

        Assert.StartsWith("Jetzt: Tagesschau (", subtitle, StringComparison.Ordinal);
        Assert.Contains("Danach: Wetter (", subtitle, StringComparison.Ordinal);
        Assert.Contains('–', subtitle);
        Assert.DoesNotContain("Das Erste", subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_IsEmptyWithoutGuide()
    {
        Assert.Equal(string.Empty, LiveTvLibraryChannelPresentation.Overview(null));
        Assert.Equal(string.Empty, LiveTvLibraryChannelPresentation.ProgramSubtitle(null));
    }

    [Fact]
    public void ProgressPercent_IsHalfWayThroughTheCurrentShow()
    {
        var start = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 13, 18, 20, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 9, 13, 18, 10, 0, DateTimeKind.Utc);

        var percent = LiveTvLibraryChannelPresentation.ProgressPercent(
            new LiveTvNowNext
            {
                NowTitle = "Tagesschau",
                NowStart = start,
                NowEnd = end
            },
            now);

        Assert.Equal(50, percent);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(25, 100)]
    public void ProgressPercent_ClampsBeforeAndAfterTheWindow(int minutesFromStart, double expected)
    {
        var start = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 13, 18, 20, 0, DateTimeKind.Utc);
        var now = start.AddMinutes(minutesFromStart);

        var percent = LiveTvLibraryChannelPresentation.ProgressPercent(
            new LiveTvNowNext { NowStart = start, NowEnd = end },
            now);

        Assert.Equal(expected, percent);
    }

    [Fact]
    public void ProgressPercent_IsNullWithoutTimes()
    {
        Assert.Null(LiveTvLibraryChannelPresentation.ProgressPercent(null, DateTime.UtcNow));
        Assert.Null(LiveTvLibraryChannelPresentation.ProgressPercent(new LiveTvNowNext { NowTitle = "X" }, DateTime.UtcNow));
    }
}
