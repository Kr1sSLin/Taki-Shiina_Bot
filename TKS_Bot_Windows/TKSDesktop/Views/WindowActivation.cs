using System.Windows;
using System.Windows.Interop;
using TKSDesktop.Platform.Windows;

namespace TKSDesktop.Views;

/// <summary>Restore visibility and focus through the same path for every entry point.</summary>
internal static class WindowActivation
{
    public static bool Restore(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        // A modal owned window must receive activation; activating its disabled
        // owner causes Windows' warning sound and leaves the dialog behind.
        var ownerHandle = new WindowInteropHelper(window).EnsureHandle();
        // ShowDialog disables the native HWND without changing WPF IsEnabled.
        var modal = window.OwnedWindows.Cast<Window>().LastOrDefault(child => child.IsVisible && child.IsEnabled);
        var target = !NativeMethods.IsWindowEnabled(ownerHandle) && modal is not null ? modal : window;
        var handle = new WindowInteropHelper(target).EnsureHandle();
        var restored = SingleInstanceGuard.ForceForeground(handle);
        var activated = target.Activate();
        return restored || activated;
    }
}
