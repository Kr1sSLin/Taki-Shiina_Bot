using TKSDesktop.Core.Services;
using TKSDesktop.ViewModels;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class ChatSearchTests
{
    [Fact]
    public void Result_HighlightsChineseSubstringAndKeepsMessageId()
    {
        var message = new ChatMessageView("message-42", "assistant", "text", "前文今天吃面后文", "sent", 0,
            null, null, null, false, false, []);

        var result = ChatSearchResult.From(message, "吃面");

        Assert.Equal("message-42", result.MessageId);
        Assert.Equal("前文今天", result.Before);
        Assert.Equal("吃面", result.Match);
        Assert.Equal("后文", result.After);
    }

    [Fact]
    public void Result_SnippetKeepsMatchInLongMessage()
    {
        var message = new ChatMessageView("long", "user", "text", new string('甲', 100) + "关键字" + new string('乙', 100),
            "sent", 0, null, null, null, false, false, []);

        var result = ChatSearchResult.From(message, "关键字");

        Assert.StartsWith("…", result.Before);
        Assert.Equal("关键字", result.Match);
        Assert.EndsWith("…", result.After);
    }
}
