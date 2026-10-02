using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Api.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Recent observations for channels visible to the current user.</summary>
[Route("LiveTv/ChannelHealth")]
[Authorize(Policy = Policies.LiveTvAccess)]
public class LiveTvHealthController : BaseJellyfinApiController
{
    private readonly ILiveTvChannelHealth _health;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="LiveTvHealthController"/> class.</summary>
    /// <param name="health">Shared channel observations.</param>
    /// <param name="library">Library access.</param>
    /// <param name="users">User access.</param>
    public LiveTvHealthController(ILiveTvChannelHealth health, ILibraryManager library, IUserManager users)
    {
        _health = health;
        _library = library;
        _users = users;
    }

    /// <summary>Reads channel states without opening or probing streams.</summary>
    /// <param name="ids">Up to 100 comma-separated Jellyfin item identifiers.</param>
    /// <returns>States keyed by item identifier, excluding inaccessible or non-channel items.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, ChannelHealth>> GetChannelHealth([FromQuery] string ids)
    {
        var userId = RequestHelpers.GetUserId(User, null);
        var user = _users.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var requested = (ids ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length > 100 || requested.Any(id => !Guid.TryParse(id, out _)))
        {
            return BadRequest("Expected at most 100 item identifiers.");
        }

        var result = new Dictionary<string, ChannelHealth>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in requested.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = _library.GetItemById<BaseItem>(Guid.Parse(id), user);
            if (item is not null && !item.IsFolder && item.ExternalId?.StartsWith("m3u", StringComparison.OrdinalIgnoreCase) == true)
            {
                result[id] = _health.GetHealth(item.ExternalId);
            }
        }

        Response.Headers.CacheControl = "no-store";
        return result;
    }
}
