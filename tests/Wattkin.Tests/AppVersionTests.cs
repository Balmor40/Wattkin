using Wattkin.Platform;
using Xunit;

namespace Wattkin.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("1.10.3+abc123", 1, 10, 3)]
    [InlineData(" V2.0 ", 2, 0, 0)]
    public void VersionsAreReadLikeTheBuildStampsThem(string text, int major, int minor, int build)
    {
        Assert.True(AppVersion.TryParse(text, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void NonVersionsAreRejected(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void TheForkHasNoNetworkCode()
    {
        // The upstream updater was removed: nothing in the app may talk to the network.
        var app = typeof(App).Assembly;
        Assert.DoesNotContain(app.GetReferencedAssemblies(), a => a.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.Requests" or "System.Net.WebClient");
        Assert.DoesNotContain(app.GetTypes(), t => t.Name is "Updater" or "Releases");
    }
}
