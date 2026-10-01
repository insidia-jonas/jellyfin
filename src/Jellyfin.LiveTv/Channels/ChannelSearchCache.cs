using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.LiveTv.Channels;

/// <summary>Briefly reuses materialized search cards across simultaneous client requests.</summary>
internal sealed class ChannelSearchCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly AsyncKeyedLocker<string> _locks = new();

    public async Task<IReadOnlyList<BaseItem>> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<BaseItem>>> fetch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await _locks.LockAsync(key, cancellationToken).ConfigureAwait(false);
        if (_cache.TryGetValue(key, out IReadOnlyList<BaseItem>? cached) && cached is not null)
        {
            return cached;
        }

        var result = await fetch(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Count > 0)
        {
            _cache.Set(key, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20),
                Size = 1
            });
        }

        return result;
    }

    public void Dispose()
    {
        _locks.Dispose();
        _cache.Dispose();
    }
}
