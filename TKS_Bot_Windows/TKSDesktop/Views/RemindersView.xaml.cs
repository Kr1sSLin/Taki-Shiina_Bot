using System.Windows;
using System.Windows.Controls;

namespace TKSDesktop.Views;

/// <summary>
/// Scheduled reminders view (FR-W-REM-6).
///
/// Code-behind only forwards the "close" and "refresh" gestures; listing and
/// cancellation are handled by the view model so the view stays free of
/// scheduling logic and never touches the database directly.
/// </summary>
public partial class RemindersView : UserControl
{
    /// <summary>构造。</summary>
    public RemindersView()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Window.GetWindow(this)?.Close();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // Manual refresh: re-reads the local `reminders` table only, no server call.
        if (DataContext is ViewModels.RemindersViewModel reminders)
        {
            await reminders.LoadAsync().ConfigureAwait(true);
        }
    }
}
