using System;
using System.Linq;
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

public class LiveTvHealthControllerTests
{
    [Fact]
    public void StatusReadsRespectUserVisibilityAndDoNotExposeProviderIdentifiers()
    {
        var users = new Mock<IUserManager>();
        var principal = TestHelpers.SetupUser(users, new Mock<IHttpContextAccessor>(), "User");
        var library = new Mock<ILibraryManager>();
        var health = new Mock<ILiveTvChannelHealth>(MockBehavior.Strict);
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var movie = Guid.NewGuid();
        library.Setup(l => l.GetItemById<BaseItem>(visible, It.IsAny<User>())).Returns(new Video { ExternalId = "m3u-channel" });
        library.Setup(l => l.GetItemById<BaseItem>(movie, It.IsAny<User>())).Returns(new Video { ExternalId = "tm-movie" });
        health.Setup(h => h.GetHealth("m3u-channel")).Returns(new ChannelHealth { Status = "Healthy" });
        var controller = new LiveTvHealthController(health.Object, library.Object, users.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        var result = controller.GetChannelHealth(string.Join(',', visible, hidden, movie)).Value;
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Healthy", result[visible.ToString()].Status);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        health.Verify(h => h.GetHealth("m3u-channel"), Times.Once);
        health.VerifyNoOtherCalls();
    }

    [Fact]
    public void RejectsOversizedOrInvalidRequestsBeforeQueryingLibrary()
    {
        var users = new Mock<IUserManager>();
        var principal = TestHelpers.SetupUser(users, new Mock<IHttpContextAccessor>(), "User");
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var controller = new LiveTvHealthController(Mock.Of<ILiveTvChannelHealth>(), library.Object, users.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } }
        };
        Assert.IsType<BadRequestObjectResult>(controller.GetChannelHealth("not-an-id").Result);
        Assert.IsType<BadRequestObjectResult>(controller.GetChannelHealth(string.Join(',', Enumerable.Repeat(Guid.NewGuid(), 101))).Result);
        library.VerifyNoOtherCalls();
    }
}
