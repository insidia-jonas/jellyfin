using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Jellyfin.Plugin.TreasureMaps.Arr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TreasureMaps.Api;

[ApiController]
[Authorize]
[Route("TreasureMaps/Requests")]
public sealed class ArrController(ArrClient client, ILibraryManager library, IUserManager users) : ControllerBase
{
    private ArrTitle? Title(Guid id)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId)) { return null; }
        var user = users.GetUserById(userId);
        if (user is null || !(user.HasPermission(PermissionKind.IsAdministrator) || user.HasPermission(PermissionKind.EnableContentDownloading))) { return null; }
        var item = library.GetItemById(id);
        if (item is null || !item.IsVisible(user)) { return null; }
        var kind = item.ProviderIds.GetValueOrDefault("TreasureMapsKind");
        if (kind is not ("tv" or "movie") && item is not (Movie or Series)) { return null; }
        return new(item.Name, kind == "tv" || item is Series, item.ProviderIds.GetValueOrDefault("Imdb") ?? string.Empty, item.ProductionYear);
    }

    [HttpGet("{itemId:guid}")]
    public async Task<IActionResult> Status(Guid itemId, CancellationToken ct)
    {
        var title = Title(itemId);
        if (title is null) { return NotFound(); }
        try { return Ok(await client.GetStatusAsync(title, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            return StatusCode(502, new { message = "Sonarr/Radarr ist derzeit nicht erreichbar." });
        }
    }

    [HttpPost("{itemId:guid}")]
    public async Task<IActionResult> RequestTitle(Guid itemId, CancellationToken ct)
    {
        var title = Title(itemId);
        if (title is null) { return NotFound(); }
        try { return Ok(await client.RequestAsync(title, ct).ConfigureAwait(false)); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            return StatusCode(502, new { message = "Sonarr/Radarr ist derzeit nicht erreichbar. Bitte erneut versuchen." });
        }
    }
}
