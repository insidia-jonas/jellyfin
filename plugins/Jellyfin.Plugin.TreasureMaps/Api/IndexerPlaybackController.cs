using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TreasureMaps.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>Resolves only verified local media; discovery cards themselves are never streams.</summary>
[ApiController]
[Authorize]
[Route("TreasureMaps/Playback")]
public sealed class IndexerPlaybackController(ILibraryManager library, IUserManager users) : ControllerBase
{
    [HttpGet("{itemId:guid}")]
    public IActionResult Resolve(Guid itemId)
    {
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId)) return NotFound();
        var user = users.GetUserById(userId);
        var item = library.GetItemById(itemId);
        if (user is null || item is null || !item.IsVisible(user) || item.SourceType != SourceType.Channel) return NotFound();
        var kind = item.ProviderIds.GetValueOrDefault("TreasureMapsKind");
        if (kind is not ("movie" or "tv")) return NotFound();
        var ids = new Dictionary<string, string[]>();
        var imdb = item.ProviderIds.GetValueOrDefault("Imdb");
        var tmdb = item.ProviderIds.GetValueOrDefault("EvolutionTmdb") ?? item.ProviderIds.GetValueOrDefault("Tmdb");
        if (!string.IsNullOrWhiteSpace(imdb)) ids["Imdb"] = [ReleaseMapper.NormalizeImdbId(imdb)];
        if (!string.IsNullOrWhiteSpace(tmdb)) ids["Tmdb"] = [tmdb];
        if (ids.Count == 0) return Ok(new { available = false });
        var candidates = library.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true, SourceTypes = [SourceType.Library], IsVirtualItem = false,
            IncludeItemTypes = [kind == "tv" ? BaseItemKind.Series : BaseItemKind.Movie],
            HasAnyProviderIds = ids, Limit = 32, EnableTotalRecordCount = false
        });
        var local = candidates.FirstOrDefault(candidate => candidate.IsVisible(user)
            && IdentityMatches(item, candidate)
            && (kind == "tv" ? Directory.Exists(candidate.Path) : System.IO.File.Exists(candidate.Path)));
        return Ok(new { available = local is not null, itemId = local?.Id.ToString("N"), isSeries = kind == "tv" });
    }

    public static bool IdentityMatches(BaseItem expected, BaseItem actual)
    {
        if (expected.ProductionYear.HasValue && actual.ProductionYear.HasValue
            && Math.Abs(expected.ProductionYear.Value - actual.ProductionYear.Value) > 1) return false;
        var matched = false;
        foreach (var key in new[] { "Imdb", "Tmdb" })
        {
            var left = expected.ProviderIds.GetValueOrDefault(key == "Tmdb" ? "EvolutionTmdb" : key) ?? expected.ProviderIds.GetValueOrDefault(key);
            var right = actual.ProviderIds.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) continue;
            var normalized = ReleaseIdentity.NormalizeId(key.ToLowerInvariant(), left);
            if (normalized.Length == 0 || normalized != ReleaseIdentity.NormalizeId(key.ToLowerInvariant(), right)) return false;
            matched = true;
        }
        // Catch old incorrectly tagged imports, too, when their filename carries a contradictory year.
        return matched && (!expected.ProductionYear.HasValue || ReleaseIdentity.SceneYear(Path.GetFileName(actual.Path)) is not int year
            || Math.Abs(year - expected.ProductionYear.Value) <= 1);
    }
}
