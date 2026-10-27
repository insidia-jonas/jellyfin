using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Configuration;

namespace Jellyfin.Plugin.TreasureMaps;

public sealed partial class TreasureMapsApiClient
{
    private readonly ConcurrentDictionary<string, (Release Release, DateTimeOffset Seen)> _knownReleases = new(StringComparer.Ordinal);

    private string VerificationKey(string guid)
    {
        var (id, _) = IndexerSource.Unscope(guid);
        var source = IndexerSource.Sources(CurrentConfig).FirstOrDefault(s => s.Id == id)
            ?? throw new InvalidOperationException("Der ursprüngliche Indexer ist nicht mehr aktiviert.");
        return SourceIdentity(source) + guid;
    }

    private void RememberReleases(IEnumerable<Release> releases)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var release in releases.Where(r => !string.IsNullOrWhiteSpace(r.Guid)))
        {
            _knownReleases[VerificationKey(release.Guid)] = (release, now);
        }
        foreach (var entry in _knownReleases.Where(e => now - e.Value.Seen > TimeSpan.FromMinutes(30)))
            _knownReleases.TryRemove(entry.Key, out _);
        if (_knownReleases.Count > 4096)
            foreach (var entry in _knownReleases.OrderBy(e => e.Value.Seen).Take(_knownReleases.Count - 4096))
                _knownReleases.TryRemove(entry.Key, out _);
    }

    /// <summary>Fail closed before fetching an NZB, including old cached channel entries and non-web callers.</summary>
    public async Task<Release> VerifyReleaseAsync(string guid, string title, bool isTv, CancellationToken ct, string? identity = null, int? year = null)
    {
        var cacheKey = VerificationKey(guid);
        if (!_knownReleases.TryGetValue(cacheKey, out var known) || DateTimeOffset.UtcNow - known.Seen > TimeSpan.FromMinutes(30))
        {
            var result = isTv
                ? await SearchTvAsync(title, 100, ct).ConfigureAwait(false)
                : await SearchMoviesAsync(title, null, 100, ct).ConfigureAwait(false);
            var exact = result?.Items.FirstOrDefault(r => string.Equals(r.Guid, guid, StringComparison.Ordinal));
            if (exact is null) throw new InvalidOperationException("Diese Version konnte nicht eindeutig geprüft werden. Bitte den Titel neu öffnen und eine aktuelle Version auswählen.");
            known = (exact, DateTimeOffset.UtcNow);
        }
        var release = known.Release;
        var kind = isTv ? "tv" : "movie";
        if (!ReleaseIdentity.HasConsistentMetadata(release)
            || ReleaseGrouper.KindOf(release) != kind
            || !SeriesBrowse.SameShow(release, identity ?? kind + ":t:", title)
            || (!isTv && year.HasValue && ReleaseIdentity.SceneYear(release.Title) is int sceneYear && Math.Abs(sceneYear - year.Value) > 1))
            throw new InvalidOperationException("Download gesperrt: Titel, Erscheinungsjahr oder Film-ID dieser Version passen nicht zum ausgewählten Titel. Der Indexer liefert widersprüchliche Daten.");
        return release;
    }
}
