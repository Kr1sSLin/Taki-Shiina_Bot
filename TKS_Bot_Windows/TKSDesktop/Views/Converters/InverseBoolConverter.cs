using System.Globalization;
using System.Windows.Data;

namespace TKSDesktop.Views.Converters;

/// <summary>布尔取反（不需要 <c>InverseBoolToVisibility</c> 时复用本转换器与参数）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
