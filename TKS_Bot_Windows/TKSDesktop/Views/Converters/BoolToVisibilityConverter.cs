using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TKSDesktop.Views.Converters;

/// <summary>
/// <see cref="bool"/> → <see cref="Visibility"/>。
///
/// 默认：<c>true</c> → <see cref="Visibility.Visible"/>，<c>false</c> → <see cref="Visibility.Collapsed"/>。
/// 参数为 <c>"Hidden"</c> 时 false → <see cref="Visibility.Hidden"/>（保留占位，避免布局跳动）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (flag)
        {
            return Visibility.Visible;
        }

        return string.Equals(parameter as string, "Hidden", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Hidden
            : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
