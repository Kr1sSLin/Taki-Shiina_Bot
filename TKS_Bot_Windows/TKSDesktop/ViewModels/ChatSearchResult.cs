using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>搜索命中的可高亮摘要，保留原消息 ID 供定位。</summary>
public sealed record ChatSearchResult(string MessageId, string Before, string Match, string After, string TimestampText)
{
    public static ChatSearchResult From(ChatMessageView message, string term)
    {
        var content = message.Content ?? string.Empty;
        var index = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var start = index < 0 ? 0 : Math.Max(0, index - 40);
        var end = index < 0 ? Math.Min(content.Length, 120) : Math.Min(content.Length, index + term.Length + 80);
        var before = (start > 0 ? "…" : string.Empty) + content[start..(index < 0 ? end : index)];
        var match = index < 0 ? string.Empty : content.Substring(index, term.Length);
        var after = index < 0 ? string.Empty : content[(index + term.Length)..end] + (end < content.Length ? "…" : string.Empty);
        if (index < 0 && end < content.Length)
        {
            before += "…";
        }

        var timestamp = MessageItemViewModel.FromUnixMilliseconds(message.Timestamp).ToString("yyyy-MM-dd HH:mm");
        return new ChatSearchResult(message.MessageId, before, match, after, timestamp);
    }
}
