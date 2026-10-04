using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>Read-only IPTV source comparisons. Never initiates a stream.</summary>
public interface IIptvWatchdog
{
    /// <summary>Returns current observations and evidence-based recommendations.</summary>
    /// <returns>A credential-free snapshot.</returns>
    IptvWatchdogStatus GetStatus();
}
