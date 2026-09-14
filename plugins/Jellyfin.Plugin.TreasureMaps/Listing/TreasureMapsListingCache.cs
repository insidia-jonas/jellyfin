using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>
/// Outcome of a listing fetch: a new payload, a validated-not-modified hit, or a
/// non-persistable result (empty / caller declined).
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
public readonly struct ListingFetch<T>
    where T : class
{
    /// <summary>Gets the payload, if any.</summary>
    public T? Value { get; init; }

    /// <summary>Gets a value indicating whether the indexer confirmed the existing snapshot is still current.</summary>
    public bool NotModified { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Value"/> may be stored as a snapshot.</summary>
    public bool Persist { get; init; }

    /// <summary>Gets an optional HTTP validator (ETag).</summary>
    public string? Validator { get; init; }

    /// <summary>Gets an optional payload hash.</summary>
    public string? Hash { get; init; }

    /// <summary>Creates a persistable snapshot.</summary>
    /// <param name="value">The payload.</param>
    /// <param name="validator">The ETag, if any.</param>
    /// <param name="hash">The payload hash, if any.</param>
    /// <returns>The fetch result.</returns>
    public static ListingFetch<T> Store(T value, string? validator = null, string? hash = null)
        => new() { Value = value, Persist = true, Validator = validator, Hash = hash };

    /// <summary>Creates a result that must not be cached (empty or error-shaped).</summary>
    /// <param name="value">The payload to return once.</param>
    /// <returns>The fetch result.</returns>
    public static ListingFetch<T> DoNotStore(T? value)
        => new() { Value = value, Persist = false };

    /// <summary>Creates a 304 / same-hash validation of the existing snapshot.</summary>
    /// <param name="validator">The ETag, if any.</param>
    /// <param name="hash">The payload hash, if any.</param>
    /// <returns>The fetch result.</returns>
    public static ListingFetch<T> Validated(string? validator = null, string? hash = null)
        => new() { NotModified = true, Validator = validator, Hash = hash };
}

/// <summary>
/// Freshness-gated snapshots for Treasure-Maps indexer browse/search. A snapshot is
/// shown only while it is still valid for the same query and a freshness check says
/// it is current. Expired or invalidated snapshots are never returned as live data.
/// Last-good-forever fallbacks are intentionally not used.
/// </summary>
public sealed class TreasureMapsListingCache
{
    /// <summary>Browse lists (<c>q=*</c> / empty / one letter).</summary>
    public static readonly TimeSpan BrowseFreshTtl = TimeSpan.FromMinutes(5);

    /// <summary>Typed search (2+ characters).</summary>
    public static readonly TimeSpan LiveSearchFreshTtl = TimeSpan.FromSeconds(20);

    /// <summary>Indexer capabilities.</summary>
    public static readonly TimeSpan CapsFreshTtl = TimeSpan.FromHours(1);

    /// <summary>TMDB spotlight feeds that are still indexer-backed.</summary>
    public static readonly TimeSpan SpotlightFreshTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long an in-flight refresh may keep serving a just-expired snapshot.</summary>
    public static readonly TimeSpan InFlightGrace = TimeSpan.FromSeconds(45);

    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<FetchBox>> _inflight = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _identity;
    private int _generation;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsListingCache"/> class.
    /// </summary>
    /// <param name="clock">The clock, or null for UTC now.</param>
    /// <param name="identity">The config/indexer identity, or null for <see cref="CurrentIdentity"/>.</param>
    public TreasureMapsListingCache(Func<DateTimeOffset>? clock = null, Func<string>? identity = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _identity = identity ?? CurrentIdentity;
    }

    /// <summary>Gets the invalidation generation (increments on <see cref="InvalidateAll"/>).</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Gets the number of stored snapshots.</summary>
    public int Count => _snapshots.Count;

    /// <summary>
    /// Wall-clock browse epoch. Folded into ChannelManager's cache key so a 3-hour
    /// disk hit cannot present yesterday's indexer rows as current.
    /// </summary>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The epoch number.</returns>
    public static long BrowseEpoch(DateTimeOffset utcNow)
        => utcNow.UtcTicks / BrowseFreshTtl.Ticks;

    /// <summary>
    /// How long before <paramref name="ttl"/> expiry a background refresh should start.
    /// </summary>
    /// <param name="ttl">The freshness TTL.</param>
    /// <returns>The lead time.</returns>
    public static TimeSpan RefreshLeadFor(TimeSpan ttl)
        => ttl >= TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(5);

    /// <summary>
    /// Identity of the current indexer + browse settings. Changing it busts every snapshot.
    /// </summary>
    /// <returns>The identity string.</returns>
    public static string CurrentIdentity()
    {
        var c = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return string.Join(
            '|',
            (c.BaseUrl ?? string.Empty).Trim().TrimEnd('/'),
            ShortSecret(c.ApiKey),
            c.PrimaryLanguage ?? string.Empty,
            string.Join(',', c.SecondaryLanguages ?? Array.Empty<string>()),
            c.FilterByLanguage ? "1" : "0",
            c.MinRating.ToString(CultureInfo.InvariantCulture),
            c.ResultLimit.ToString(CultureInfo.InvariantCulture),
            c.EnableXrel ? "1" : "0",
            string.IsNullOrWhiteSpace(c.OmdbApiKey) ? "0" : "1");
    }

    /// <summary>
    /// Returns true when a snapshot may be shown as current for <paramref name="key"/>.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="value">The snapshot value.</param>
    /// <param name="shouldRefresh">Whether a background refresh should start.</param>
    /// <returns>True when the snapshot is fresh or still within an in-flight validated window.</returns>
    public bool TryGetFresh<T>(string key, out T? value, out bool shouldRefresh)
        where T : class
    {
        value = default;
        shouldRefresh = false;
        if (string.IsNullOrEmpty(key) || !_snapshots.TryGetValue(key, out var snap))
        {
            return false;
        }

        if (!string.Equals(snap.Identity, _identity(), StringComparison.Ordinal)
            || snap.Value is not T typed
            || IsEmptyListing(typed))
        {
            return false;
        }

        var now = _clock();
        if (now < snap.FreshUntil)
        {
            shouldRefresh = now >= snap.FreshUntil - RefreshLeadFor(snap.Ttl);
            value = typed;
            return true;
        }

        if (now < snap.FreshUntil + InFlightGrace && _inflight.ContainsKey(key))
        {
            value = typed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a still-fresh snapshot immediately. When the snapshot is missing or expired,
    /// starts/coalesces a background fetch and returns false — the caller must not present
    /// expired rows as current and must not wait for indexer HTTP.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="freshTtl">How long a new snapshot stays fresh.</param>
    /// <param name="fetch">The indexer fetch (runs in the background on a miss).</param>
    /// <param name="value">The fresh payload, if any.</param>
    /// <returns>True when <paramref name="value"/> may be shown as current.</returns>
    public bool TryGetFreshOrSchedule<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch,
        out T? value)
        where T : class
    {
        if (TryGetFresh<T>(key, out value, out var shouldRefresh))
        {
            if (shouldRefresh)
            {
                ScheduleRefresh(key, freshTtl, fetch);
            }

            return true;
        }

        ScheduleRefresh(key, freshTtl, fetch);
        value = default;
        return false;
    }

    /// <summary>
    /// Starts a coalesced background refresh for <paramref name="key"/>.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="freshTtl">How long a new snapshot stays fresh.</param>
    /// <param name="fetch">The indexer fetch.</param>
    public void ScheduleRefresh<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch)
        where T : class
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        ScheduleRefreshCore(key, freshTtl, fetch);
    }

    /// <summary>
    /// Returns a fresh snapshot when one is valid; otherwise fetches, coalescing in-flight
    /// requests for the same key. Expired snapshots are not returned unless a fetch
    /// validates them (304 / same hash) or a refresh is already in flight inside the grace window.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="key">The query key.</param>
    /// <param name="freshTtl">How long a new snapshot stays fresh.</param>
    /// <param name="fetch">The indexer fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The payload, or null when the fetch produced nothing persistable.</returns>
    public async Task<T?> GetOrFetchAsync<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch,
        CancellationToken cancellationToken)
        where T : class
    {
        if (TryGetFresh<T>(key, out var hit, out var shouldRefresh))
        {
            if (shouldRefresh)
            {
                ScheduleRefresh(key, freshTtl, fetch);
            }

            return hit;
        }

        var box = await CoalesceAsync(key, freshTtl, fetch, cancellationToken).ConfigureAwait(false);
        return box.Value as T;
    }

    /// <summary>
    /// Gets the stored ETag/validator for a key when the snapshot still matches the current identity.
    /// Used for conditional GET — not a health-check extra connection.
    /// </summary>
    /// <param name="key">The query key.</param>
    /// <returns>The validator, or null.</returns>
    public string? GetValidator(string key)
    {
        if (!_snapshots.TryGetValue(key, out var snap)
            || !string.Equals(snap.Identity, _identity(), StringComparison.Ordinal))
        {
            return null;
        }

        return snap.Validator;
    }

    /// <summary>
    /// Drops every snapshot. ChannelManager keys that include <see cref="Generation"/> miss
    /// immediately; the next open waits for a fresh fetch (or warmup).
    /// </summary>
    public void InvalidateAll()
    {
        _snapshots.Clear();
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Drops one query key.
    /// </summary>
    /// <param name="key">The query key.</param>
    public void Invalidate(string key)
    {
        _snapshots.TryRemove(key, out _);
    }

    /// <summary>
    /// True when a listing payload is empty and must not be stored as success.
    /// </summary>
    /// <param name="value">The payload.</param>
    /// <returns>True when the payload is an empty release list.</returns>
    public static bool IsEmptyListing(object? value)
    {
        if (value is ReleaseListResponse list)
        {
            return list.Items is null || list.Items.Count == 0;
        }

        if (value is MediaBrowser.Controller.Channels.ChannelItemResult folder)
        {
            return folder.RefreshPending || folder.Items is null || folder.Items.Count == 0;
        }

        return false;
    }

    /// <summary>
    /// Stable hash of a listing payload (guids / serialized body).
    /// </summary>
    /// <param name="value">The payload.</param>
    /// <returns>The hex hash, or null.</returns>
    public static string? HashPayload(object? value)
    {
        if (value is null || IsEmptyListing(value))
        {
            return null;
        }

        if (value is ReleaseListResponse list)
        {
            var sb = new StringBuilder(list.Items.Count * 24);
            foreach (var item in list.Items)
            {
                if (item is null)
                {
                    continue;
                }

                sb.Append(item.Guid).Append('\n').Append(item.Title).Append('\n');
                if (item.PostedAt.HasValue)
                {
                    sb.Append(item.PostedAt.Value.UtcTicks.ToString(CultureInfo.InvariantCulture));
                }

                sb.Append('|');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        if (value is MediaBrowser.Controller.Channels.ChannelItemResult folder)
        {
            var sb = new StringBuilder((folder.Items?.Count ?? 0) * 24);
            foreach (var item in folder.Items ?? Array.Empty<MediaBrowser.Controller.Channels.ChannelItemInfo>())
            {
                if (item is null)
                {
                    continue;
                }

                sb.Append(item.Id).Append('\n').Append(item.Name).Append('|');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        try
        {
            return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ScheduleRefreshCore<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch)
        where T : class
    {
        if (_inflight.ContainsKey(key))
        {
            return;
        }

        _ = CoalesceAsync(key, freshTtl, fetch, CancellationToken.None);
    }

    private Task<FetchBox> CoalesceAsync<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch,
        CancellationToken cancellationToken)
        where T : class
    {
        var task = _inflight.GetOrAdd(key, _ => RunFetchAsync(key, freshTtl, fetch, cancellationToken));
        _ = ForgetInflight(key, task);
        return task;
    }

    private async Task ForgetInflight(string key, Task<FetchBox> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The awaiting caller observes the exception; this only clears the slot.
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Task<FetchBox>>(key, task));
        }
    }

    private async Task<FetchBox> RunFetchAsync<T>(
        string key,
        TimeSpan freshTtl,
        Func<string?, CancellationToken, Task<ListingFetch<T>>> fetch,
        CancellationToken cancellationToken)
        where T : class
    {
        var identity = _identity();
        var validator = GetValidator(key);
        var previousHash = _snapshots.TryGetValue(key, out var existing)
            && string.Equals(existing.Identity, identity, StringComparison.Ordinal)
            ? existing.Hash
            : null;

        var result = await fetch(validator, cancellationToken).ConfigureAwait(false);
        if (result.NotModified
            || (!string.IsNullOrEmpty(result.Hash) && string.Equals(result.Hash, previousHash, StringComparison.Ordinal)))
        {
            if (_snapshots.TryGetValue(key, out var snap)
                && string.Equals(snap.Identity, identity, StringComparison.Ordinal)
                && snap.Value is T)
            {
                snap.FreshUntil = _clock().Add(freshTtl);
                snap.Ttl = freshTtl;
                snap.Validator = result.Validator ?? snap.Validator;
                snap.Hash = result.Hash ?? snap.Hash;
                return new FetchBox { Value = snap.Value };
            }

            if (result.Value is T validated)
            {
                Store(key, identity, validated, freshTtl, result.Validator, result.Hash);
                return new FetchBox { Value = validated };
            }

            return new FetchBox();
        }

        if (!result.Persist || result.Value is null || IsEmptyListing(result.Value))
        {
            return new FetchBox { Value = result.Value };
        }

        var hash = result.Hash ?? HashPayload(result.Value);
        Store(key, identity, result.Value, freshTtl, result.Validator, hash);
        return new FetchBox { Value = result.Value };
    }

    private void Store(string key, string identity, object value, TimeSpan ttl, string? validator, string? hash)
    {
        if (!string.Equals(identity, _identity(), StringComparison.Ordinal))
        {
            return;
        }

        if (_snapshots.Count >= MaxEntries)
        {
            Evict();
        }

        _snapshots[key] = new Snapshot
        {
            Value = value,
            Identity = identity,
            FreshUntil = _clock().Add(ttl),
            Ttl = ttl,
            Validator = validator,
            Hash = hash
        };
    }

    private void Evict()
    {
        var now = _clock();
        foreach (var pair in _snapshots)
        {
            if (pair.Value.FreshUntil < now)
            {
                _snapshots.TryRemove(pair.Key, out _);
            }
        }

        if (_snapshots.Count < MaxEntries)
        {
            return;
        }

        _snapshots.Clear();
    }

    private static string ShortSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "0";
        }

        var take = Math.Min(4, value.Length);
        return value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value[^take..];
    }

    private sealed class Snapshot
    {
        public required object Value { get; init; }

        public required string Identity { get; init; }

        public DateTimeOffset FreshUntil { get; set; }

        public TimeSpan Ttl { get; set; }

        public string? Validator { get; set; }

        public string? Hash { get; set; }
    }

    private sealed class FetchBox
    {
        public object? Value { get; init; }
    }
}
