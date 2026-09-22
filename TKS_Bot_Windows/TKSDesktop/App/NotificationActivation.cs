namespace TKSDesktop.App;

/// <summary>通知协议只携带动作与消息标识，不接受路径或可执行命令。</summary>
public sealed record NotificationActivation(string Action, string? MessageId)
{
    public const string Scheme = "tksdesktop";

    public static string BuildUri(string action, string? messageId)
        => $"{Scheme}://notification?action={Uri.EscapeDataString(action)}&messageId={Uri.EscapeDataString(messageId ?? string.Empty)}";

    public static NotificationActivation? Parse(string value)
    {
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Scheme || uri.Host != "notification"
            || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length != 0)
        {
            return null;
        }

        var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var action = parameters["action"];
        var messageId = parameters["messageId"];
        if (action is not ("open" or "reply") || messageId?.Length > 512)
        {
            return null;
        }

        return new(action, string.IsNullOrWhiteSpace(messageId) ? null : messageId);
    }
}
