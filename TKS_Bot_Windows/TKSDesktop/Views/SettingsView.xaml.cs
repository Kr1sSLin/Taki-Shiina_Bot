using System.Windows;
using System.Windows.Controls;

namespace TKSDesktop.Views;

/// <summary>
/// Settings view (FR-W-SET-1..12).
///
/// Every control binds to <c>SettingsViewModel</c>; the code-behind only forwards
/// the "close" gesture. Directory-opening and export actions go through the view
/// model so that path handling stays on the <c>IShellLauncher</c> abstraction
/// (FR-W-SEC-5 forbids string-concatenated <c>Process.Start</c>).
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>构造。</summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Window.GetWindow(this)?.Close();
}
