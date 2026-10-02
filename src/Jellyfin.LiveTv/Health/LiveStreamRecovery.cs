using System;

namespace Jellyfin.LiveTv.Health;

/// <summary>A short recovery episode; a stable stream resets the budget, first bytes do not.</summary>
internal sealed class LiveStreamRecovery
{
    private DateTime? _firstFailure;

    internal int Failures { get; private set; }

    internal bool Failed(DateTime now, TimeSpan dataDuration)
    {
        if (dataDuration >= TimeSpan.FromSeconds(15))
        {
            Failures = 0;
            _firstFailure = null;
        }

        _firstFailure ??= now;
        Failures++;
        return Failures < 4 && now - _firstFailure.Value < TimeSpan.FromSeconds(40);
    }

    internal bool ShouldSwitch => Failures > 0 && Failures % 2 == 0;
}
