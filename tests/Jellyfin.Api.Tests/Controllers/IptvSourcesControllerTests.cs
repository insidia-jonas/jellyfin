using System;
using Jellyfin.Api.Controllers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class IptvSourcesControllerTests
{
    [Fact]
    public void ChoicesRequireVisibleChannelAndNeverQueryHiddenItems()
    {
        var users = new Mock<IUserManager>();
        var principal = TestHelpers.SetupUser(users, new Mock<IHttpContextAccessor>(), "User");
        var library = new Mock<ILibraryManager>();
        var sources = new Mock<IIptvSourceSelector>(MockBehavior.Strict);
        var visible = Guid.NewGuid();
        var movie = Guid.NewGuid();
        library.Setup(l => l.GetItemById<BaseItem>(visible, It.IsAny<User>())).Returns(new Video { ExternalId = "m3u-channel" });
        library.Setup(l => l.GetItemById<BaseItem>(movie, It.IsAny<User>())).Returns(new Video { ExternalId = "movie" });
        var choices = new IptvChannelSources { AutomaticMediaSourceId = "base" };
        sources.Setup(s => s.GetSources("m3u-channel", "handle")).Returns(choices);
        var controller = new IptvSourcesController(sources.Object, library.Object, users.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        Assert.Same(choices, controller.GetSources(visible, "handle").Value);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<NotFoundResult>(controller.GetSources(Guid.NewGuid()).Result);
        Assert.IsType<NotFoundResult>(controller.GetSources(movie).Result);
        sources.Verify(s => s.GetSources("m3u-channel", "handle"), Times.Once);
        sources.VerifyNoOtherCalls();
    }
}
