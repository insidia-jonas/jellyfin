using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

public sealed partial class TreasureMapsApiClient
{
    private static string SourceIdentity(IndexerSource source)
        => source.Id + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source)))) + ":";

    private static void SetOrigin(ReleaseListResponse list, IndexerSource source)
    {
        foreach (var release in list.Items)
        {
            release.Guid = source.Scope(release.Guid);
            release.IndexerId = source.Id;
            release.IndexerName = source.Name;
            // The browser never needs a provider's download URL (it can contain its API key).
            release.Links = null;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, Dictionary<string, string?>? parameters, TimeSpan cacheTtl, CancellationToken ct)
        where T : class
    {
        var sources = IndexerSource.Sources(CurrentConfig);
        if (sources.Count == 0) { throw new InvalidOperationException("Kein Indexer eingerichtet oder aktiviert."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Leave time for relevance ranking/materialization inside the six-second search budget.
        if (sources.Count > 1) { deadline.CancelAfter(TimeSpan.FromMilliseconds(4500)); }
        var tasks = sources.Select(async source =>
        {
            try
            {
                return await FetchSourceAsync<T>(source, path, parameters, cacheTtl, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or XmlException or InvalidOperationException)
            {
                _logger.LogDebug("Indexer {IndexerId}: {FailureType} during {Operation}", source.Id, ex.GetType().Name, path);
                return null;
            }
        }).ToArray();
        var values = (await Task.WhenAll(tasks).ConfigureAwait(false)).Where(v => v is not null).ToArray();
        ct.ThrowIfCancellationRequested();
        if (values.Length == 0) { throw new HttpRequestException("Die aktivierten Indexer sind momentan nicht erreichbar."); }
        if (typeof(T) == typeof(ReleaseListResponse))
        {
            var lists = values.Cast<ReleaseListResponse>().ToArray();
            var items = lists.SelectMany(l => l.Items).DistinctBy(r => r.Guid).ToArray();
            var pageSize = int.TryParse(parameters?.GetValueOrDefault("limit"), out var requested) ? requested : 100;
            var offset = int.TryParse(parameters?.GetValueOrDefault("offset"), out var start) ? start : 0;
            return new ReleaseListResponse { Items = items, NextOffset = offset + pageSize,
                HasMore = lists.Any(l => l.Pagination?.Total > offset + pageSize || l.Pagination == null && l.Items.Count >= pageSize), Pagination = new Pagination
            {
                Limit = lists.Max(l => l.Pagination?.Limit ?? l.Items.Count),
                Offset = lists.Max(l => l.Pagination?.Offset ?? 0),
                Total = (int)Math.Min(int.MaxValue, lists.Sum(l => (long)(l.Pagination?.Total ?? l.Items.Count)))
            } } as T;
        }
        if (typeof(T) == typeof(CapsResponse))
        {
            var caps = values.Cast<CapsResponse>().ToArray();
            return new CapsResponse
            {
                Genres = caps.SelectMany(c => c.Genres).DistinctBy(g => g.Id).ToArray(),
                Categories = caps.SelectMany(c => c.Categories).DistinctBy(g => g.Id).ToArray()
            } as T;
        }
        return values[0];
    }

    private Task<T?> FetchSourceAsync<T>(IndexerSource source, string path, Dictionary<string, string?>? parameters, TimeSpan ttl, CancellationToken ct)
        where T : class
        => source.Protocol == "newznab" ? FetchNewznabAsync<T>(source, path, parameters, ttl, ct) : FetchRestAsync<T>(source, path, parameters, ttl, ct);

    /// <summary>Validates credentials with a read-only authenticated request, without changing configuration.</summary>
    public async Task TestSourceAsync(IndexerSource source, CancellationToken ct)
    {
        source.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var user = await FetchSourceAsync<UserInfoResponse>(source, "user", null, TimeSpan.Zero, deadline.Token).ConfigureAwait(false);
        if (user?.User == null) { throw new InvalidOperationException("Indexer hat keine gültige Antwort geliefert."); }
    }
}
