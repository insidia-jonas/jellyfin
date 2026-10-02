using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>Reads shared channel observations; reading never starts a provider connection.</summary>
public interface ILiveTvChannelHealth
{
    /// <summary>Returns a snapshot for a tuner channel identifier.</summary>
    /// <param name="channelId">The external tuner channel identifier.</param>
    /// <returns>The recent channel status.</returns>
    ChannelHealth GetHealth(string channelId);
}
