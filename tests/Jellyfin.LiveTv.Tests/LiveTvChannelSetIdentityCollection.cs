#pragma warning disable CA1711 // This is an xUnit collection definition.

using Xunit;

namespace Jellyfin.LiveTv.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveTvChannelSetIdentityCollection
{
    public const string Name = "LiveTvChannelSetIdentity";
}
