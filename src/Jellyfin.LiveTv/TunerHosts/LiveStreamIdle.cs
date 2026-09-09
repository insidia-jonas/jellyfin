using System;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Idle-timeout math for tuner live streams.
/// </summary>
internal static class LiveStreamIdle
{
    /// <summary>
    /// Returns true when the stream was opened, has no readers, and the grace period has elapsed.
    /// </summary>
    /// <param name="activeReaders">Current file readers.</param>
    /// <param name="dateOpened">When <see cref="LiveStream.Open"/> completed.</param>
    /// <param name="lastReaderReleasedUtc">When the last reader disposed, or default if none.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns><c>true</c> if the IPTV connection should be torn down.</returns>
    public static bool IsIdle(int activeReaders, DateTime dateOpened, DateTime lastReaderReleasedUtc, DateTime utcNow)
    {
        if (activeReaders > 0 || dateOpened == default)
        {
            return false;
        }

        var last = lastReaderReleasedUtc == default ? dateOpened : lastReaderReleasedUtc;
        return utcNow - last >= LiveStream.IdleGrace;
    }
}
