using System.Windows;

namespace TKSDesktop.Views;

/// <summary>Inherited visual policy, independent of each panel's DataContext.</summary>
public static class VisualPreferences
{
    public static readonly DependencyProperty UseOpaqueFallbackProperty = DependencyProperty.RegisterAttached(
        "UseOpaqueFallback", typeof(bool), typeof(VisualPreferences),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetUseOpaqueFallback(DependencyObject target) => (bool)target.GetValue(UseOpaqueFallbackProperty);

    public static void SetUseOpaqueFallback(DependencyObject target, bool value) => target.SetValue(UseOpaqueFallbackProperty, value);
}
