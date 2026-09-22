using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TKSDesktop.Views.Converters;

/// <summary>
/// <c>null</c> / 空白字符串 → <see cref="Visibility.Collapsed"/>，否则 <see cref="Visibility.Visible"/>。
/// 参数为 <c>"Invert"</c> 时语义反转（用于「空态提示」这类需要「为空才显示」的场景）。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string text && string.IsNullOrWhiteSpace(text));
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);

        var visible = invert ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
