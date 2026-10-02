using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class ChannelNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryCardsStayAccessibleWithoutPollutingCategoryRoot(bool latest)
    {
        using var fixture = new Fixture();
        var discovered = latest
            ? (await fixture.Manager.GetLatestChannelItemsInternal(new InternalItemsQuery { ChannelIds = [fixture.Root.Id] }, TestContext.Current.CancellationToken)).Items
            : await fixture.Manager.SearchChannelItemsAsync("Title", null, 10, TestContext.Current.CancellationToken);

        var title = Assert.Single(discovered);
        Assert.Equal(Guid.Empty, title.ParentId);
        Assert.Equal(fixture.Root.Id, title.ChannelId);
        Assert.Same(title, fixture.Items[title.Id]);
        var root = await fixture.Browse();
        Assert.Equal("Categories", Assert.Single(root.Items).Name);
        Assert.Same(title, fixture.Items[title.Id]);
        fixture.VerifyNoDeletes();
    }

    [Fact]
    public async Task DiscoveryPreservesAnExistingBrowseParent()
    {
        using var fixture = new Fixture();
        var title = fixture.Seed<BoxSet>("title", Guid.NewGuid());
        var parent = title.ParentId;

        var discovered = await fixture.Manager.SearchChannelItemsAsync("Title", null, 10, TestContext.Current.CancellationToken);

        Assert.Same(title, Assert.Single(discovered));
        Assert.Equal(parent, title.ParentId);
        fixture.VerifyNoDeletes();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRequestsDetachStaleCardsOnceWithoutDeletingTheirDetails(bool nested)
    {
        using var fixture = new Fixture();
        BaseItem parent = fixture.Root;
        if (nested)
        {
            parent = fixture.Seed<Folder>("movies", fixture.Root.Id);
            fixture.Seed<Folder>("categories", parent.Id).Name = "Categories";
        }

        var title = fixture.Seed<BoxSet>("old-latest", parent.Id);
        var child = fixture.Seed<Folder>("release", title.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Library.Setup(x => x.UpdateItemAsync(title, null!, ItemUpdateType.None, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await finish.Task.WaitAsync(TestContext.Current.CancellationToken);
            });

        var first = fixture.Browse(parent, limit: 1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var second = fixture.Browse(parent, limit: 1);
        try
        {
            Assert.False(second.IsCompleted);
            fixture.Provider.Verify(x => x.GetChannelItems(It.IsAny<InternalChannelItemQuery>(), It.IsAny<CancellationToken>()), Times.Once());
        }
        finally
        {
            finish.TrySetResult();
        }

        foreach (var result in await Task.WhenAll(first, second))
        {
            Assert.Equal("Categories", Assert.Single(result.Items).Name);
        }

        Assert.Equal(Guid.Empty, title.ParentId);
        Assert.Equal(title.Id, child.ParentId);
        fixture.Library.Verify(x => x.UpdateItemAsync(title, null!, ItemUpdateType.None, It.IsAny<CancellationToken>()), Times.Once());
        fixture.VerifyNoDeletes();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        public Fixture()
        {
            Provider.SetupGet(x => x.Name).Returns("Navigation test");
            Provider.SetupGet(x => x.DataVersion).Returns("1");
            Provider.As<IChannelPresentationOverlay>();
            var title = new ChannelItemInfo { Id = "title", Name = "Title", Type = ChannelItemType.Folder, FolderType = ChannelFolderType.BoxSet };
            Provider.As<ISupportsLatestMedia>().Setup(x => x.GetLatestMedia(It.IsAny<ChannelLatestMediaSearch>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { title });
            Provider.As<ISupportsSearch>().Setup(x => x.GetSearchResults(It.IsAny<ChannelSearchInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { title });
            Library.Setup(x => x.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
                .Returns((string key, Type type) => (key + type.Name).GetMD5());
            Root = new Channel { Id = Library.Object.GetNewItemId("Channel Navigation test", typeof(Channel)), Name = "Navigation test" };
            Root.ChannelId = Root.Id;
            Items[Root.Id] = Root;
            Seed<Folder>("categories", Root.Id).Name = "Categories";
            Library.Setup(x => x.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => Items.GetValueOrDefault(id)!);
            Library.Setup(x => x.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
                .Callback((BaseItem item, BaseItem _) => Items[item.Id] = item);
            Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => Children(query));
            Library.Setup(x => x.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => Children(query).OrderBy(x => x.ExternalId).Take(query.Limit ?? int.MaxValue).Select(x => x.Id).ToArray());
            Library.Setup(x => x.GetItemsResult(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => new QueryResult<BaseItem>(0, Children(query).Length, Children(query)));
            Provider.Setup(x => x.GetChannelItems(It.IsAny<InternalChannelItemQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelItemResult { Items = [new() { Id = "categories", Name = "Categories", Type = ChannelItemType.Folder }] });
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(x => x.ApplicationPaths.CachePath).Returns(Path.GetTempPath());
            Manager = new ChannelManager(
                Mock.Of<IUserManager>(),
                Mock.Of<IDtoService>(),
                Library.Object,
                NullLogger<ChannelManager>.Instance,
                config.Object,
                Mock.Of<IFileSystem>(),
                Mock.Of<IUserDataManager>(),
                Mock.Of<IProviderManager>(),
                _cache,
                [Provider.Object]);
        }

        public Mock<IChannel> Provider { get; } = new();

        public Mock<ILibraryManager> Library { get; } = new();

        public ConcurrentDictionary<Guid, BaseItem> Items { get; } = new();

        public Channel Root { get; }

        public ChannelManager Manager { get; }

        public T Seed<T>(string externalId, Guid parentId)
            where T : BaseItem
        {
            var mock = new Mock<T> { CallBase = true };
            mock.Setup(x => x.OnMetadataChanged()).Returns(ItemUpdateType.None);
            mock.Setup(x => x.UpdateToRepositoryAsync(It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var item = mock.Object;
            item.Id = Library.Object.GetNewItemId(externalId + "Navigation test16", typeof(T));
            item.ExternalId = externalId;
            item.ChannelId = Root.Id;
            item.ParentId = parentId;
            Items[item.Id] = item;
            return item;
        }

        public Task<QueryResult<BaseItem>> Browse(BaseItem? parent = null, int? limit = null) => Manager.GetChannelItemsInternal(
            new InternalItemsQuery { Parent = parent ?? Root, ChannelIds = [Root.Id], Limit = limit }, new Progress<double>(), TestContext.Current.CancellationToken);

        public void VerifyNoDeletes() => Library.Verify(x => x.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never());

        public void Dispose()
        {
            Manager.Dispose();
            _cache.Dispose();
        }

        private BaseItem[] Children(InternalItemsQuery query) => Items.Values.Where(x => x.ParentId.Equals(query.ParentId) && !x.Id.Equals(Root.Id)).ToArray();
    }
}
