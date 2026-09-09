using System;
using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class LiveStreamIdleTests
{
    [Fact]
    public void IsIdle_NeverOpened_IsFalse()
    {
        Assert.False(LiveStreamIdle.IsIdle(0, default, default, DateTime.UtcNow));
    }

    [Fact]
    public void IsIdle_ActiveReader_IsFalse()
    {
        var opened = DateTime.UtcNow.AddMinutes(-5);
        Assert.False(LiveStreamIdle.IsIdle(1, opened, default, DateTime.UtcNow));
    }

    [Fact]
    public void IsIdle_JustOpened_IsFalse()
    {
        var now = DateTime.UtcNow;
        Assert.False(LiveStreamIdle.IsIdle(0, now, default, now.AddSeconds(10)));
    }

    [Fact]
    public void IsIdle_OpenedPastGrace_IsTrue()
    {
        var opened = DateTime.UtcNow.AddMinutes(-5);
        Assert.True(LiveStreamIdle.IsIdle(0, opened, default, DateTime.UtcNow));
    }

    [Fact]
    public void IsIdle_ReaderReleasedPastGrace_IsTrue()
    {
        var opened = DateTime.UtcNow.AddMinutes(-10);
        var released = DateTime.UtcNow.AddMinutes(-2);
        Assert.True(LiveStreamIdle.IsIdle(0, opened, released, DateTime.UtcNow));
    }

    [Fact]
    public void IsIdle_ReaderReleasedRecently_IsFalse()
    {
        var opened = DateTime.UtcNow.AddMinutes(-10);
        var now = DateTime.UtcNow;
        Assert.False(LiveStreamIdle.IsIdle(0, opened, now.AddSeconds(5), now));
    }
}
