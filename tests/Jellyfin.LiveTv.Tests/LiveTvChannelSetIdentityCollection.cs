using Xunit;

namespace Jellyfin.LiveTv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveTvChannelSetIdentityCollection
{
    public const string Name = "LiveTvChannelSetIdentity";
}
