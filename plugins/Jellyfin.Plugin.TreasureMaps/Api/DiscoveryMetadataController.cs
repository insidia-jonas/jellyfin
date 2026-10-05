using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Metadata;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TreasureMaps.Api;

[ApiController]
[Authorize]
[Route("TreasureMaps/Metadata")]
public sealed class DiscoveryMetadataController(MetadataCatalog catalog, ILibraryManager library, IUserManager users) : ControllerBase
{
    private ReleaseGroup? Resolve(Guid id)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId)) return null;
        var user = users.GetUserById(userId);
        var item = library.GetItemById(id);
        if (user is null || item is null || !item.IsVisible(user)) return null;
        var kind = item.ProviderIds.GetValueOrDefault("TreasureMapsKind");
        if (kind is not ("movie" or "tv")) return null;
        // The visible item's identity is authoritative, never arbitrary client URLs or IMDb ids.
        return new ReleaseGroup { Title = item.Name, Kind = kind, Year = item.ProductionYear,
            Imdb = item.ProviderIds.GetValueOrDefault("Imdb"), Tmdb = item.ProviderIds.GetValueOrDefault("EvolutionTmdb") };
    }

    [HttpPost("Warm")]
    public IActionResult Warm([FromBody] Guid[] ids)
    {
        if (ids.Length > 24) return BadRequest();
        catalog.Warm(ids.Distinct().Select(Resolve).OfType<ReleaseGroup>());
        return Accepted();
    }

    [HttpGet("{itemId:guid}")]
    public async Task<IActionResult> Details(Guid itemId, CancellationToken ct)
    {
        var group = Resolve(itemId);
        if (group is null) return NotFound();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var hit = await catalog.GetAsync(group, deadline.Token).ConfigureAwait(false);
            return Ok(new { available = hit is not null, title = hit?.Title, overview = hit?.Plot,
                rating = hit?.Rating, ratingSource = hit?.RatingSource, language = hit?.Language,
                year = hit?.Year ?? group.Year, imdb = group.Imdb, genres = hit?.Genres });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(202, new { pending = true }); }
        catch (System.Net.Http.HttpRequestException) { return StatusCode(503, new { available = false }); }
    }
}
