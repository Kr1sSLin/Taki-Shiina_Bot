using System.Windows;
using System.Windows.Media;

namespace TKSDesktop.Views;

/// <summary>
/// 应用级 UI 缩放覆盖（FR-W-UI-10）。WPF 的窗口几何仍以 DIP 持久化，
/// 视觉树和实际窗口尺寸按同一因子缩放，避免启动参数只被解析而不生效。
/// </summary>
internal static class UiScale
{
    private const double Minimum = 0.5d;
    private const double Maximum = 3.0d;

    public static double Normalize(double? value)
        => value is { } factor
            && double.IsFinite(factor)
            && factor > 0
            ? Math.Clamp(factor, Minimum, Maximum)
            : 1d;

    public static void Apply(FrameworkElement root, double factor)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.LayoutTransform = factor == 1d
            ? Transform.Identity
            : new ScaleTransform(factor, factor);
    }

    public static double ToWindow(double logicalValue, double factor) => logicalValue * factor;

    public static double ToLogical(double windowValue, double factor)
        => factor == 0d ? windowValue : windowValue / factor;
}
