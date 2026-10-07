using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Search;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

public sealed class MediaSearchSqlTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<JellyfinDbContext> _options;
    private readonly SqlSearchProvider _provider;
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IItemQueryHelpers> _access = new();
    private readonly Guid _movie = Guid.NewGuid();
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _cached = Guid.NewGuid();
    private readonly Guid _download = Guid.NewGuid();
    private readonly Guid _library = Guid.NewGuid();

    public MediaSearchSqlTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.BaseItems.AddRange(
            new BaseItemEntity { Id = _library, Type = "CollectionFolder", IsFolder = true },
            new BaseItemEntity { Id = _movie, Type = "Movie", IsMovie = true, Name = "Insidious: Out of the Further", CleanName = "insidious out of the further", ProductionYear = 2026, ParentId = _library, Overview = "A mother enters a mysterious realm." },
            new BaseItemEntity { Id = _series, Type = "Series", IsSeries = true, Name = "All Her Fault", CleanName = "all her fault", ProductionYear = 2025, Overview = "Marissa discovers Carries past." },
            new BaseItemEntity { Id = _cached, Type = "BoxSet", ChannelId = Guid.NewGuid(), Name = "Insidious: Out of the Further", CleanName = "insidious out of the further", ProductionYear = 2026 },
            new BaseItemEntity { Id = _download, Type = "Folder", ChannelId = Guid.NewGuid(), Name = "1080p WEBRip", CleanName = "1080p webrip", OriginalTitle = "Insidious: Out of the Further", Provider = [new() { Item = null!, ProviderId = "TreasureMapsKind", ProviderValue = "movie" }] });
        db.SaveChanges();
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateContext);
        var lookup = new Mock<IItemTypeLookup>();
        lookup.SetupGet(l => l.BaseItemKindNames).Returns(new Dictionary<BaseItemKind, string>
        {
            [BaseItemKind.Movie] = "Movie",
            [BaseItemKind.Series] = "Series",
            [BaseItemKind.BoxSet] = "BoxSet"
        });
        _provider = new SqlSearchProvider(factory.Object, lookup.Object, Mock.Of<ILibraryManager>(), _users.Object, _access.Object);
    }

    [Theory]
    [InlineData("Insidious 6 - Out of the Further", true)]
    [InlineData("film: Insidious 6 - Out of the Further 2026", true)]
    [InlineData("All Her Fualt", false)]
    [InlineData("Fault All Her", false)]
    [InlineData("Marissa Carrie", false)]
    public async Task RealSqlFindsAliasTypoWordOrderAndFullText(string term, bool movie)
    {
        var results = await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = term }, TestContext.Current.CancellationToken);
        Assert.Equal(movie ? _movie : _series, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task CachedIndexerCopiesDoNotPolluteGlobalLibraryResults()
    {
        var results = await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = "Insidious" }, TestContext.Current.CancellationToken);
        Assert.Equal(_movie, Assert.Single(results).ItemId);
        Assert.DoesNotContain(results, r => r.ItemId.Equals(_cached));
        Assert.DoesNotContain(results, r => r.ItemId.Equals(_download));
    }

    [Fact]
    public async Task OrdinaryLiveChannelsRemainSearchable()
    {
        using var db = CreateContext();
        var channel = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Video", ChannelId = Guid.NewGuid(), Name = "Kabel Eins Doku", CleanName = "kabel eins doku" };
        db.BaseItems.Add(channel);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var results = await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = "Kabel Eins Doku" }, TestContext.Current.CancellationToken);
        Assert.Equal(channel.Id, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task FiltersRemainAuthoritativeForFuzzyAndFullTextCandidates()
    {
        Assert.Empty(await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = "All Her Fualt", ParentId = _library }, TestContext.Current.CancellationToken));
        Assert.Empty(await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = "Marissa Carrie", IncludeItemTypes = [BaseItemKind.Movie] }, TestContext.Current.CancellationToken));
        Assert.Empty(await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = "Insidious 1999" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FuzzyAndFullTextNeverBypassTheUsersAccessFilter()
    {
        var user = new User("viewer", "test", "test");
        _users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        _access.Setup(a => a.ApplyAccessFiltering(It.IsAny<JellyfinDbContext>(), It.IsAny<IQueryable<BaseItemEntity>>(), It.IsAny<InternalItemsQuery>()))
            .Returns((JellyfinDbContext _, IQueryable<BaseItemEntity> items, InternalItemsQuery _) => items.Where(i => i.ParentId.Equals(_library)));
        foreach (var text in new[] { "All Her Fualt", "Marissa Carrie" })
        {
            Assert.Empty(await _provider.SearchAsync(new SearchProviderQuery { SearchTerm = text, UserId = user.Id }, TestContext.Current.CancellationToken));
        }

        _access.Verify(a => a.ApplyAccessFiltering(It.IsAny<JellyfinDbContext>(), It.IsAny<IQueryable<BaseItemEntity>>(), It.IsAny<InternalItemsQuery>()), Times.Exactly(2));
    }

    public void Dispose() => _connection.Dispose();

    private JellyfinDbContext CreateContext() => new(
        _options,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
