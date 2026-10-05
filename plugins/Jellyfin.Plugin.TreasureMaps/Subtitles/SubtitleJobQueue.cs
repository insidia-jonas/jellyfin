using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Durable, single-worker subtitle jobs. Request cancellation never owns the worker.</summary>
public sealed class SubtitleJobQueue : BackgroundService
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Func<SubtitleJob, Action<SubtitleProgress>, CancellationToken, Task> _run;
    private readonly ILogger<SubtitleJobQueue> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<SubtitleJob> _jobs;
    private CancellationTokenSource? _current;
    private Guid _currentId;
    private string? _storageError;
    public string? StorageError => _storageError;

    public SubtitleJobQueue(string path, Func<SubtitleJob, Action<SubtitleProgress>, CancellationToken, Task> run, ILogger<SubtitleJobQueue> logger)
    {
        _path = path;
        _run = run;
        _logger = logger;
        // A corrupt store must fail visibly instead of silently losing billable job history.
        _jobs = new();
        try { if (File.Exists(path)) _jobs = JsonSerializer.Deserialize<List<SubtitleJob>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _storageError = "Untertitel-Warteschlange nicht lesbar. Der Administrator muss den Auftragsspeicher prüfen.";
            _logger.LogError("Subtitle history could not be read ({ErrorType}); new work is disabled", ex.GetType().Name);
            return;
        }
        for (var i = 0; i < _jobs.Count; i++)
        {
            if (_jobs[i].Active)
                _jobs[i] = _jobs[i] with { State = "interrupted", Updated = DateTimeOffset.UtcNow,
                    Message = "Server neu gestartet. Gesicherte Schritte bleiben erhalten. Bitte Fortsetzen bestätigen." };
        }
        if (_jobs.Count > 0)
        {
            try { Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageFailed(ex); }
        }
    }

    public SubtitleJob Enqueue(Guid owner, Guid item, SubtitleQuote quote, bool force)
    {
        lock (_sync)
        {
            if (_storageError is not null) throw new InvalidOperationException(_storageError);
            var duplicate = _jobs.FirstOrDefault(j => j.ItemId == item && j.Quote.Language == quote.Language && j.Active);
            if (duplicate is not null) return duplicate;
            if (_jobs.Count(j => j.Active) >= 8) throw new InvalidOperationException("Die Warteschlange ist voll (maximal acht Aufträge).");
            var job = new SubtitleJob { Id = Guid.NewGuid(), Owner = owner, ItemId = item, Quote = quote, Force = force };
            _jobs.Add(job);
            try { Save(); }
            catch (Exception ex) { _jobs.Remove(job); StorageFailed(ex); throw; }
            _signal.Release();
            return job;
        }
    }

    public SubtitleJob[] Snapshot()
    {
        lock (_sync) return _jobs.OrderByDescending(j => j.Created).ToArray();
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || !job.Active) return false;
            Update(job with { State = _currentId == id ? "cancelling" : "cancelled", Message = "Abbruch angefordert. Bereits angefallene Anbieter-Kosten bleiben bestehen." });
            if (_currentId == id) _current?.Cancel();
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_storageError is not null) return;
        try { await WorkAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A damaged/full job store must disable subtitle work, never stop Jellyfin.
            lock (_sync) { StorageFailed(ex); }
        }
    }

    private void StorageFailed(Exception ex)
    {
        _storageError = "Untertitel-Warteschlange kann nicht gespeichert werden. Speicherplatz und Schreibrechte prüfen.";
        _logger.LogError("Subtitle worker disabled after storage failure ({ErrorType})", ex.GetType().Name);
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
            SubtitleJob? job;
            CancellationTokenSource work;
            lock (_sync)
            {
                job = _jobs.FirstOrDefault(j => j.State == "queued");
                if (job is null) continue;
                work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                work.CancelAfter(TimeSpan.FromHours(4));
                _current = work;
                _currentId = job.Id;
                Update(job with { State = "running", Message = "Auftrag wird vorbereitet" });
            }
            try
            {
                await _run(job, progress =>
                {
                    lock (_sync)
                    {
                        var latest = _jobs.First(j => j.Id == job.Id);
                        if (latest.State == "cancelling") return;
                        Update(latest with { Stage = progress.Stage, Percent = Math.Max(latest.Percent, Math.Clamp(progress.Percent, 0, 99)), Message = progress.Message });
                    }
                }, work.Token).ConfigureAwait(false);
                Finish(job.Id, "completed", "Untertitel gespeichert. Sie stehen nach der Medienaktualisierung zur Auswahl bereit.", 100);
            }
            catch (OperationCanceledException)
            {
                var cancelled = Snapshot().Any(j => j.Id == job.Id && j.State == "cancelling");
                Finish(job.Id, stoppingToken.IsCancellationRequested ? "interrupted" : cancelled ? "cancelled" : "failed",
                    stoppingToken.IsCancellationRequested ? "Server beendet. Gesicherte Schritte können nach Bestätigung fortgesetzt werden."
                    : cancelled ? "Abgebrochen. Gesicherte Schritte bleiben erhalten." : "Zeitlimit erreicht. Gesicherte Schritte können fortgesetzt werden.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Subtitle job {JobId} failed ({ErrorType})", job.Id, ex.GetType().Name);
                Finish(job.Id, "failed", SafeError(ex));
            }
            finally
            {
                lock (_sync) { _current = null; _currentId = Guid.Empty; }
                work.Dispose();
            }
        }
    }

    private void Finish(Guid id, string state, string message, int? percent = null)
    {
        lock (_sync)
        {
            var job = _jobs.First(j => j.Id == id);
            Update(job with { State = state, Message = message, Percent = percent ?? job.Percent });
        }
    }

    private void Update(SubtitleJob job)
    {
        _jobs[_jobs.FindIndex(j => j.Id == job.Id)] = job with { Updated = DateTimeOffset.UtcNow };
        Save();
    }

    private void Save()
    {
        var obsolete = _jobs.Where(j => !j.Active).OrderByDescending(j => j.Updated).Skip(100).ToArray();
        foreach (var job in obsolete) _jobs.Remove(job);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_jobs));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(_path + ".tmp", _path, true);
    }

    public static string SafeError(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.StartsWith("Untertitel-Warteschlange", StringComparison.Ordinal)) return ex.Message;
        if (ex is InvalidOperationException && ex.Message.StartsWith("Die Warteschlange ist voll", StringComparison.Ordinal)) return ex.Message;
        if (ex is OperationCanceledException or TimeoutException) return "Der KI-Anbieter hat nicht rechtzeitig geantwortet. Gesicherte Schritte bleiben erhalten; bitte später erneut versuchen.";
        if (ex is UnauthorizedAccessException) return "Die Untertiteldatei konnte nicht gespeichert werden. Schreibrechte des Medienordners prüfen.";
        var status = Regex.Match(ex.Message, @"(?:HTTP |returned )(\d{3})\b");
        if (status.Success) return "KI-Anbieter meldet HTTP " + status.Groups[1].Value + ". API-Schlüssel, Modell und Guthaben prüfen. Gesicherte Schritte bleiben erhalten.";
        if (ex.Message.Contains("translation", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("cue", StringComparison.OrdinalIgnoreCase))
            return "Die Übersetzung enthielt unvollständige Untertitel. Es wurde keine fehlerhafte Datei gespeichert. Erneut versuchen nutzt die gesicherte Spracherkennung.";
        if (ex.Message.Contains("Keine Sprache", StringComparison.Ordinal)) return "Keine Sprache erkannt. Es wurde keine leere Untertiteldatei gespeichert.";
        return "Untertitelauftrag fehlgeschlagen. Medienzugriff und KI-Konfiguration prüfen. Gesicherte Schritte bleiben erhalten.";
    }
}

/// <summary>Private persisted state, including source path. Only View() may leave the server.</summary>
public sealed record SubtitleJob
{
    public Guid Id { get; init; }
    public Guid Owner { get; init; }
    public Guid ItemId { get; init; }
    public SubtitleQuote Quote { get; init; } = new();
    public bool Force { get; init; }
    public string State { get; init; } = "queued";
    public string Stage { get; init; } = "queued";
    public int Percent { get; init; }
    public string Message { get; init; } = "Wartet auf einen freien Platz. Du kannst die Filmseite verlassen.";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; init; } = DateTimeOffset.UtcNow;
    public bool Active => State is "queued" or "running" or "cancelling";
    public object View() => new { id = Id, itemId = ItemId, title = Quote.Title, language = Quote.Language,
        state = State, stage = Stage, percent = Percent, message = Message, active = Active,
        estimatedUsd = Quote.TotalUsd, created = Created, updated = Updated };
}
