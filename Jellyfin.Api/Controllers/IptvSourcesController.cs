using System;
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

/// <summary>IPTV choices limited to channels visible to the current viewer.</summary>
[Route("LiveTv/Channels/{itemId}/Sources")]
[Authorize(Policy = Policies.LiveTvAccess)]
public class IptvSourcesController : BaseJellyfinApiController
{
    private readonly IIptvSourceSelector _sources;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="IptvSourcesController"/> class.</summary>
    /// <param name="sources">Configured choices and cached evidence.</param>
    /// <param name="library">Authorized item resolution.</param>
    /// <param name="users">Current user.</param>
    public IptvSourcesController(IIptvSourceSelector sources, ILibraryManager library, IUserManager users)
    {
        _sources = sources;
        _library = library;
        _users = users;
    }

    /// <summary>Reads server choices without probing or opening provider connections.</summary>
    /// <param name="itemId">Jellyfin channel item.</param>
    /// <param name="liveStreamId">Optional currently playing stream handle.</param>
    /// <returns>Server choices, without provider URLs or credentials.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IptvChannelSources> GetSources([FromRoute] Guid itemId, [FromQuery] string? liveStreamId = null)
    {
        var user = _users.GetUserById(RequestHelpers.GetUserId(User, null));
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _library.GetItemById<BaseItem>(itemId, user);
        if (item is null || item.IsFolder || item.ExternalId?.StartsWith("m3u", StringComparison.OrdinalIgnoreCase) != true)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return _sources.GetSources(item.ExternalId, liveStreamId);
    }
}
