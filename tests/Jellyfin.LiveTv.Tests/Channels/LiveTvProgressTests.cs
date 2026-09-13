using System;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class LiveTvProgressTests
{
    [Fact]
    public void GetPercent_IsNullWhenTimesAreMissing()
    {
        var now = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        Assert.Null(LiveTvProgress.GetPercent(null, now.AddMinutes(15), now));
        Assert.Null(LiveTvProgress.GetPercent(now, null, now));
        Assert.Null(LiveTvProgress.GetPercent(now.AddMinutes(15), now, now));
    }

    [Fact]
    public void Apply_WritesStartEndAndPercentOntoTheDto()
    {
        var start = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 13, 19, 0, 0, DateTimeKind.Utc);
        var dto = new BaseItemDto();

        LiveTvProgress.Apply(dto, start, end, start.AddMinutes(15));

        Assert.Equal(start, dto.StartDate);
        Assert.Equal(end, dto.EndDate);
        Assert.Equal(25, dto.CompletionPercentage);
    }
}
