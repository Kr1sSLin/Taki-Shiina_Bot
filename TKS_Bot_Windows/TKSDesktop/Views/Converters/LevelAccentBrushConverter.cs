using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TKSDesktop.Contracts;

namespace TKSDesktop.Views.Converters;

/// <summary>
/// 等级码 → 强调色画刷（等级说明 / 徽章 / 进度条共用 —— C-1 <see cref="LevelVisuals"/>）。
///
/// ⚠️ 必须容忍空 / 未知 <c>levelCode</c>（<see cref="LevelVisuals.Resolve"/> 回中性兜底 —— EDGE-W-24）；
///    颜色字符串非法时回退为透明（**不抛错** —— NFR-W-12）。
/// </summary>
public sealed class LevelAccentBrushConverter : IValueConverter
{
    /// <summary>可选的强调色缓存（等级数固定，避免每次转换都解析字符串）。</summary>
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    /// <summary>参数可为 <c>"From"</c> / <c>"To"</c> / <c>"Accent"</c>（默认 <c>Accent</c>）。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visual = LevelVisuals.Resolve(value as string);

        var color = (parameter as string) switch
        {
            "From" => visual.GradientFrom,
            "To" => visual.GradientTo,
            _ => visual.Accent,
        };

        return BrushFor(color);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把 `#RRGGBB` / `#AARRGGBB` 解析为冻结画刷；非法值回退透明。</summary>
    internal static Brush BrushFor(string? color)
    {
        var key = color ?? string.Empty;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        Brush brush = Brushes.Transparent;

        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(key);
                if (converted is Color parsed)
                {
                    var solid = new SolidColorBrush(parsed);
                    solid.Freeze();
                    brush = solid;
                }
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                // 契约色值非法 → 透明兜底（不抛错）。
                brush = Brushes.Transparent;
            }
        }

        lock (Cache)
        {
            Cache[key] = brush;
        }

        return brush;
    }
}
