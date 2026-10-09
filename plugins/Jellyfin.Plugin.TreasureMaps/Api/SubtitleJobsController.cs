using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TreasureMaps.Api;

[ApiController]
[Authorize]
[Route("TreasureMaps/Subtitles")]
public sealed class SubtitleJobsController(SubtitleJobQueue jobs, AiSubtitleService ai, ILibraryManager library, IUserManager users, SubtitleSyncService sync) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    private BaseItem? Visible(Guid id, bool write = false)
    {
        var user = users.GetUserById(UserId);
        var item = library.GetItemById(id);
        if (user is null || item is not (Movie or Episode) || !item.IsVisible(user)) return null;
        if (write && !user.HasPermission(PermissionKind.IsAdministrator) && !user.HasPermission(PermissionKind.EnableContentDownloading)) return null;
        return item;
    }

    [HttpGet("Jobs")]
    public IActionResult List([FromQuery] Guid? itemId = null)
        => Ok(new { error = jobs.StorageError, jobs = jobs.Snapshot().Where(j => j.Owner == UserId && (!itemId.HasValue || j.ItemId == itemId.Value)
            && Visible(j.ItemId) is not null).Take(40).Select(j => j.View()).ToArray() });

    [HttpPost("Jobs/{id:guid}/Cancel")]
    public IActionResult Cancel(Guid id)
    {
        var job = jobs.Snapshot().FirstOrDefault(j => j.Id == id && j.Owner == UserId);
        if (job is null || Visible(job.ItemId, true) is null) return NotFound();
        return Ok(new { ok = jobs.Cancel(id) });
    }

    [HttpPost("Generate")]
    public async Task<IActionResult> Generate([FromQuery] Guid itemId, [FromQuery] string? language,
        [FromQuery] bool force, [FromQuery] bool confirmed, [FromQuery] decimal? maxEstimatedUsd, CancellationToken ct)
    {
        var item = Visible(itemId, true);
        if (item is null) return NotFound();
        var path = SubtitleFiles.ResolveMediaPath(item);
        if (path is null) return BadRequest(new { ok = false, message = "Der Film muss zuerst vollständig heruntergeladen sein." });
        var lang = LanguageMatcher.Normalize(language);
        if (string.IsNullOrEmpty(lang)) lang = "de";
        try
        {
            if (!force && SubtitleFiles.TryRead(path, lang, out _))
            {
                item.ChangedExternally();
                return Ok(new { ok = true, alreadyExists = true, totalUsd = 0m });
            }
            if (!AiSubtitleService.IsEnabled) return BadRequest(new { ok = false, message = "KI-Untertitel sind nicht konfiguriert." });
            if (!confirmed || maxEstimatedUsd is null or < 0) return BadRequest(new { ok = false, message = "Bitte zuerst die Kostenschätzung bestätigen." });
            var quote = await ai.QuoteAsync(path, lang, item.Name, item.RunTimeTicks, ct).ConfigureAwait(false);
            if (quote.TotalUsd > maxEstimatedUsd) return Conflict(new { ok = false, message = "Die Kostenschätzung hat sich geändert. Bitte erneut prüfen und bestätigen." });
            ct.ThrowIfCancellationRequested();
            var job = jobs.Enqueue(UserId, itemId, quote, force);
            return Accepted(new { ok = true, job = job.View() });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return BadRequest(new { ok = false, message = SubtitleJobQueue.SafeError(ex) });
        }
    }

    [HttpGet("Sync")]
    public IActionResult SyncOptions([FromQuery] Guid itemId)
    {
        var item = Visible(itemId, true);
        return item is null ? NotFound() : Ok(sync.Options(item));
    }

    [HttpPost("Sync")]
    public IActionResult Synchronize([FromQuery] Guid itemId, [FromQuery] int subtitleIndex, [FromQuery] int audioIndex)
    {
        var item = Visible(itemId, true);
        if (item is null) return NotFound();
        if (!SubtitleSyncService.Available) return BadRequest(new { ok = false, message = "Tonspur-Abgleich ist noch nicht installiert." });
        var path = SubtitleFiles.ResolveMediaPath(item);
        if (path is null) return BadRequest(new { ok = false, message = "Lokale Mediendatei fehlt." });
        try
        {
            var (subtitle, audio) = sync.Tracks(item, subtitleIndex, audioIndex);
            var quote = new SubtitleQuote { Path = path, Language = LanguageMatcher.Normalize(subtitle.Language), Title = item.Name };
            return Accepted(new { ok = true, job = jobs.Enqueue(UserId, itemId, quote, false, subtitleIndex, audioIndex, SubtitleSyncService.Fingerprint(path, subtitle, audio)).View() });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, message = SubtitleJobQueue.SafeError(ex) }); }
    }
}
