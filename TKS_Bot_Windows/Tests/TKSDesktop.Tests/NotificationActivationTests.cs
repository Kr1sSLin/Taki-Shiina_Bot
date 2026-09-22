using TKSDesktop.App;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class NotificationActivationTests
{
    [Theory]
    [InlineData("open", "msg_12")]
    [InlineData("reply", "中文 & = # ? / %")]
    [InlineData("open", null)]
    public void NotificationRoundTrip_PreservesMessageAndAction(string action, string? messageId)
    {
        var options = CliOptions.Parse([NotificationActivation.BuildUri(action, messageId)]);
        Assert.Equal(new NotificationActivation(action, messageId), options.Notification);
        Assert.Empty(options.Unknown);
        Assert.False(options.ResetConfig);
    }

    [Theory]
    [InlineData("https://notification?action=open&messageId=x")]
    [InlineData("tksdesktop://other?action=open")]
    [InlineData("tksdesktop://notification?action=delete")]
    [InlineData("tksdesktop://notification/path?action=open")]
    [InlineData("tksdesktop://user@notification?action=open")]
    public void InvalidNotificationUri_DoesNotActivate(string value)
        => Assert.Null(NotificationActivation.Parse(value));
}
