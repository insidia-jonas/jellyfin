using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Shared grab pipeline: downloads a release's NZB from the indexer and queues it in SABnzbd
/// with the category matching the media type. Used by the favorite handler, the API controller
/// and the channel's play-to-download callback; de-duplicates repeated grabs per session.
/// </summary>
public class GrabService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly TreasureMapsApiClient _client;
    private readonly SabnzbdClient _sabnzbd;
    private readonly ILogger<GrabService> _logger;
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GrabRecord> _byNzo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GrabRecord> _byName = new(StringComparer.Ordinal);
    private readonly object _persistLock = new();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrabService"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="logger">The logger.</param>
    public GrabService(TreasureMapsApiClient client, SabnzbdClient sabnzbd, ILogger<GrabService> logger)
    {
        _client = client;
        _sabnzbd = sabnzbd;
        _logger = logger;
    }

    /// <summary>
    /// Grabs a release into SABnzbd. Repeated calls for the same guid in one session are ignored.
    /// </summary>
    /// <param name="guid">The release guid.</param>
    /// <param name="name">The SABnzbd job name (preferably the movie/show title).</param>
    /// <param name="isTv">Whether the release is a TV release (category selection).</param>
    /// <param name="coverUrl">The title's cover URL (shown on the Downloads tile), may be null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="displayTitle">The human title when <paramref name="name"/> is a quality badge.</param>
    /// <param name="quality">The quality badge for the Downloads overview.</param>
    /// <returns>The created SABnzbd job ids (empty when skipped as duplicate).</returns>
    public async Task<IReadOnlyList<string>> GrabAsync(
        string guid,
        string name,
        bool isTv,
        string? coverUrl,
        CancellationToken cancellationToken,
        string? displayTitle = null,
        string? quality = null)
    {
        if (!_handled.TryAdd(guid, 0))
        {
            _logger.LogDebug("Grab for {Guid} skipped (already grabbed this session)", guid);
            return Array.Empty<string>();
        }

        try
        {
            if (!SabnzbdClient.IsConfigured)
            {
                throw new InvalidOperationException("SABnzbd is not configured.");
            }

            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var config = Plugin.Instance!.Configuration;
            var preferred = isTv ? config.SabnzbdTvCategory : config.SabnzbdMovieCategory;
            var category = string.IsNullOrWhiteSpace(preferred) ? config.SabnzbdCategory : preferred;

            var title = DownloadTitle.Resolve(name, displayTitle);
            var jobName = title == "Download" ? Sanitize(string.IsNullOrWhiteSpace(name) ? guid : name) : Sanitize(title);
            var ids = await _sabnzbd.AddNzbAsync(payload, jobName, category, cancellationToken).ConfigureAwait(false);
            RegisterGrab(ids, jobName, coverUrl, title, quality, guid, isTv ? "tv" : "movie");
            _logger.LogInformation("Grabbed '{Name}' into SABnzbd category '{Category}' ({Ids})", title, category, string.Join(",", ids));
            return ids;
        }
        catch
        {
            _handled.TryRemove(guid, out _);
            throw;
        }
    }

    /// <summary>
    /// Looks up the cover registered for a download (by SABnzbd job id, falling back to the
    /// normalized job name).
    /// </summary>
    /// <param name="nzoId">The SABnzbd job id.</param>
    /// <param name="name">The job name.</param>
    /// <returns>The cover URL, or null.</returns>
    public string? GetArtwork(string? nzoId, string? name) => Lookup(nzoId, name)?.CoverUrl;

    /// <summary>
    /// Returns the persisted grab record for a SABnzbd job, if this plugin queued it.
    /// </summary>
    /// <param name="nzoId">The SABnzbd job id.</param>
    /// <param name="name">The job name.</param>
    /// <returns>The record, or null.</returns>
    public GrabRecord? Lookup(string? nzoId, string? name)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(nzoId) && _byNzo.TryGetValue(nzoId, out var byId))
        {
            return byId;
        }

        var key = NameKey(name);
        return key.Length > 0 && _byName.TryGetValue(key, out var byName) ? byName : null;
    }

    /// <summary>
    /// Whether this SABnzbd job was started from Treasure-Maps (and should appear in Downloads).
    /// </summary>
    /// <param name="nzoId">The SABnzbd job id.</param>
    /// <param name="name">The job name.</param>
    /// <returns>True when the job is a Treasure-Maps grab.</returns>
    public bool IsTracked(string? nzoId, string? name) => Lookup(nzoId, name) is not null;

    /// <summary>
    /// SABnzbd job ids registered for this title (every duplicate grab of the same movie/show).
    /// </summary>
    /// <param name="nzoId">A known job id, may be null.</param>
    /// <param name="name">The job or display title, may be null.</param>
    /// <returns>The matching nzo ids.</returns>
    public IReadOnlyList<string> ListNzoIds(string? nzoId, string? name)
    {
        EnsureLoaded();
        var rec = Lookup(nzoId, name);
        var titleKey = NameKey(rec?.Title ?? name);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(nzoId))
        {
            ids.Add(nzoId);
        }

        foreach (var record in _byNzo.Values.Concat(_byName.Values))
        {
            if (string.IsNullOrWhiteSpace(record.NzoId) || !SameGrab(record, nzoId, titleKey))
            {
                continue;
            }

            ids.Add(record.NzoId);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Drops a title from Downloads: forgets the grab records and removes the SABnzbd
    /// queue/history rows. Downloaded video files are left on disk.
    /// </summary>
    /// <param name="nzoId">A SABnzbd job id, may be null when <paramref name="name"/> is set.</param>
    /// <param name="name">The job or display title, may be null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many SABnzbd jobs were asked to be deleted.</returns>
    public async Task<int> RemoveFromDownloadsAsync(string? nzoId, string? name, CancellationToken cancellationToken)
    {
        var ids = ListNzoIds(nzoId, name);
        foreach (var id in ids)
        {
            if (!SabnzbdClient.IsConfigured)
            {
                break;
            }

            try
            {
                await _sabnzbd.RemoveJobAsync(id, fromHistory: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SABnzbd history delete failed for {Id}", id);
            }

            try
            {
                await _sabnzbd.RemoveJobAsync(id, fromHistory: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SABnzbd queue delete failed for {Id}", id);
            }
        }

        Forget(nzoId, name);
        return ids.Count;
    }

    /// <summary>
    /// Forgets grab records for a title so it no longer appears in Downloads.
    /// </summary>
    /// <param name="nzoId">A SABnzbd job id, may be null.</param>
    /// <param name="name">The job or display title, may be null.</param>
    /// <returns>How many nzo-keyed records were removed.</returns>
    public int Forget(string? nzoId, string? name)
    {
        EnsureLoaded();
        var rec = Lookup(nzoId, name);
        var titleKey = NameKey(rec?.Title ?? name);
        var removed = 0;

        foreach (var key in _byNzo.Keys.ToList())
        {
            if (!_byNzo.TryGetValue(key, out var record) || !SameGrab(record, nzoId, titleKey))
            {
                continue;
            }

            if (_byNzo.TryRemove(key, out _))
            {
                removed++;
            }
        }

        foreach (var key in _byName.Keys.ToList())
        {
            if (!_byName.TryGetValue(key, out var record))
            {
                continue;
            }

            if (SameGrab(record, nzoId, titleKey)
                || string.Equals(key, titleKey, StringComparison.Ordinal)
                || string.Equals(key, NameKey(name), StringComparison.Ordinal))
            {
                _byName.TryRemove(key, out _);
            }
        }

        Persist();
        return removed;
    }

    private static bool SameGrab(GrabRecord record, string? nzoId, string? titleKey)
    {
        if (!string.IsNullOrEmpty(nzoId) && string.Equals(record.NzoId, nzoId, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(titleKey)
            && (string.Equals(NameKey(record.Title), titleKey, StringComparison.Ordinal)
                || string.Equals(record.NameKey, titleKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Recent Treasure-Maps grabs, newest first, one row per title.
    /// </summary>
    /// <param name="max">The maximum number of records.</param>
    /// <returns>The grab records.</returns>
    public IReadOnlyList<GrabRecord> ListRecent(int max)
    {
        EnsureLoaded();
        return _byNzo.Values
            .Concat(_byName.Values)
            .GroupBy(r =>
            {
                var titleKey = NameKey(r.Title);
                return titleKey.Length > 0 ? titleKey : (r.NzoId ?? r.NameKey ?? string.Empty);
            }, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.GrabbedAt).First())
            .OrderByDescending(r => r.GrabbedAt)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Registers the cover for a set of SABnzbd jobs (used by grab paths that queue directly).
    /// </summary>
    /// <param name="nzoIds">The SABnzbd job ids.</param>
    /// <param name="name">The job name.</param>
    /// <param name="coverUrl">The cover URL, may be null.</param>
    public void RegisterArtwork(IReadOnlyList<string> nzoIds, string name, string? coverUrl)
        => RegisterGrab(nzoIds, name, coverUrl, DownloadTitle.Resolve(name, null), null, null, null);

    /// <summary>
    /// Remembers a Treasure-Maps grab so Downloads can filter SABnzbd and show the real title.
    /// </summary>
    /// <param name="nzoIds">The SABnzbd job ids.</param>
    /// <param name="name">The job name (and extra name key).</param>
    /// <param name="coverUrl">The cover URL, may be null.</param>
    /// <param name="title">The movie/show title.</param>
    /// <param name="quality">The quality badge, may be null.</param>
    /// <param name="guid">The indexer guid, may be null.</param>
    /// <param name="kind">movie or tv, may be null.</param>
    public void RegisterGrab(
        IReadOnlyList<string> nzoIds,
        string name,
        string? coverUrl,
        string? title,
        string? quality,
        string? guid,
        string? kind)
    {
        EnsureLoaded();
        var resolved = DownloadTitle.Resolve(name, title);
        var key = NameKey(name);
        var now = DateTime.UtcNow;
        var ids = (nzoIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0)
        {
            ids.Add(string.Empty);
        }

        foreach (var id in ids)
        {
            var record = new GrabRecord
            {
                NzoId = string.IsNullOrEmpty(id) ? null : id,
                NameKey = key,
                Title = resolved,
                CoverUrl = string.IsNullOrWhiteSpace(coverUrl) ? Lookup(id, name)?.CoverUrl : coverUrl,
                Kind = kind,
                Guid = guid,
                Quality = quality,
                GrabbedAt = now
            };

            if (!string.IsNullOrEmpty(record.NzoId))
            {
                _byNzo[record.NzoId] = record;
            }

            if (key.Length > 0)
            {
                _byName[key] = record;
            }

            var titleKey = NameKey(resolved);
            if (titleKey.Length > 0)
            {
                _byName[titleKey] = record;
            }
        }

        Persist();
    }

    /// <summary>
    /// Normalizes a job or title name for artwork / tracking lookups.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <returns>Letters and digits only, lower-case.</returns>
    public static string NameKey(string? name)
        => new string((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_persistLock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var path = StorePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var records = JsonSerializer.Deserialize<List<GrabRecord>>(json, JsonOptions) ?? new List<GrabRecord>();
                    foreach (var record in records)
                    {
                        if (!string.IsNullOrEmpty(record.NzoId))
                        {
                            _byNzo[record.NzoId] = record;
                        }

                        if (!string.IsNullOrEmpty(record.NameKey))
                        {
                            _byName[record.NameKey] = record;
                        }

                        var titleKey = NameKey(record.Title);
                        if (titleKey.Length > 0)
                        {
                            _byName[titleKey] = record;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Treasure-Maps grab history");
            }

            _loaded = true;
        }
    }

    private void Persist()
    {
        lock (_persistLock)
        {
            try
            {
                var path = StorePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var records = _byNzo.Values
                    .Concat(_byName.Values)
                    .GroupBy(r => r.NzoId ?? r.NameKey ?? r.Title)
                    .Select(g => g.OrderByDescending(r => r.GrabbedAt).First())
                    .OrderByDescending(r => r.GrabbedAt)
                    .Take(500)
                    .ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist Treasure-Maps grab history");
            }
        }
    }

    private static string StorePath()
    {
        var dir = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return Path.Combine(Path.GetTempPath(), "treasuremaps-grabs.json");
        }

        return Path.Combine(dir, "grabs.json");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
