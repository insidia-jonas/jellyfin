using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>Bounds requests across browse, search and warmup and respects provider cooldowns.</summary>
public sealed class IndexerRequestGate : IDisposable
{
    private readonly SemaphoreSlim _slots = new(3, 3);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private DateTimeOffset _blockedUntil;

    /// <summary>Initializes a request gate.</summary>
    /// <param name="clock">Optional test clock.</param>
    public IndexerRequestGate(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Acquires an HTTP slot, or fails promptly during an upstream cooldown.</summary>
    /// <param name="cancellationToken">Cancellation for the queued request.</param>
    /// <returns>A lease that releases the slot.</returns>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        CheckCooldown();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCooldown();
            return new Lease(_slots);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    /// <summary>Defers further requests after a 429 or temporary gateway failure.</summary>
    /// <param name="duration">Retry-After duration, bounded to one minute.</param>
    public void BackOff(TimeSpan duration)
    {
        var until = _clock.GetUtcNow().AddSeconds(Math.Clamp(duration.TotalSeconds, 1, 60));
        lock (_gate)
        {
            if (until > _blockedUntil) _blockedUntil = until;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();

    private void CheckCooldown()
    {
        lock (_gate)
        {
            if (_clock.GetUtcNow() < _blockedUntil)
                throw new HttpRequestException("Treasure-Maps is temporarily rate limited. Please retry shortly.", null, HttpStatusCode.TooManyRequests);
        }
    }

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) slots.Release();
        }
    }
}
