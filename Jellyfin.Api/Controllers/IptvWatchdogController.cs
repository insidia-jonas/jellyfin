using MediaBrowser.Common.Api;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Administrative IPTV source observations. Reading never starts a test.</summary>
[Route("LiveTv/Watchdog")]
[Authorize(Policy = Policies.RequiresElevation)]
public class IptvWatchdogController : BaseJellyfinApiController
{
    private readonly IIptvWatchdog _watchdog;

    /// <summary>Initializes a new instance of the <see cref="IptvWatchdogController"/> class.</summary>
    /// <param name="watchdog">Source observations.</param>
    public IptvWatchdogController(IIptvWatchdog watchdog) => _watchdog = watchdog;

    /// <summary>Reads the idle state, recent source evidence and server recommendations.</summary>
    /// <returns>The current snapshot without provider URLs or credentials.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IptvWatchdogStatus> GetWatchdogStatus()
    {
        Response.Headers.CacheControl = "no-store";
        return _watchdog.GetStatus();
    }
}
