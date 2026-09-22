using System.Globalization;
using System.Windows.Data;

namespace TKSDesktop.Views.Converters;

/// <summary>
/// 消息时间格式化（PRD §5.5 **C-4** / FR-W-CHAT-13）。
///
/// 规则（**必须与另外两端逐字一致**）：
/// <list type="bullet">
///   <item>同日：`HH:mm`</item>
///   <item>跨日（同一年）：`MM-DD HH:mm`</item>
///   <item>更早（不同年）：`YYYY-MM-DD HH:mm`</item>
/// </list>
///
/// <para>输入可为 <see cref="DateTimeOffset"/> / <see cref="DateTime"/> / Unix 毫秒（<see cref="long"/>）；
/// 无法识别时回退为空串（**不抛错** —— NFR-W-12）。</para>
/// </summary>
public sealed class TimeFormat : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var cultureInfo = culture ?? CultureInfo.CurrentCulture;
        var moment = ToLocalTime(value);

        if (moment is null)
        {
            return string.Empty;
        }

        var now = DateTimeOffset.Now;

        if (moment.Value.Date == now.Date)
        {
            return moment.Value.ToString("HH:mm", cultureInfo);
        }

        return moment.Value.Year == now.Year
            ? moment.Value.ToString("MM-dd HH:mm", cultureInfo)
            : moment.Value.ToString("yyyy-MM-dd HH:mm", cultureInfo);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把任意支持的时间输入归一为本地时间；不可识别时返回 <c>null</c>。</summary>
    private static DateTimeOffset? ToLocalTime(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case DateTimeOffset offset:
                return offset.ToLocalTime();

            case DateTime dateTime:
                {
                    var normalized = dateTime.Kind switch
                    {
                        DateTimeKind.Utc => new DateTimeOffset(dateTime),
                        DateTimeKind.Local => new DateTimeOffset(dateTime),
                        _ => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local)),
                    };

                    return normalized.ToLocalTime();
                }

            case long milliseconds:
                return SafeFromUnixMilliseconds(milliseconds);

            case int milliseconds:
                return SafeFromUnixMilliseconds(milliseconds);

            case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return SafeFromUnixMilliseconds(parsed);

            default:
                return null;
        }
    }

    private static DateTimeOffset? SafeFromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
