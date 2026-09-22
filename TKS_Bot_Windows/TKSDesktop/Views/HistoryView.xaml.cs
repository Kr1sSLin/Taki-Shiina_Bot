using System.Windows;
using System.Windows.Controls;

namespace TKSDesktop.Views;

/// <summary>
/// History &amp; memory view (FR-W-HIS-1..5).
///
/// Code-behind only forwards the "close" gesture to the hosting window; all data
/// loading and read-only enforcement live in the view model, so nothing here
/// touches the database or the network.
/// </summary>
public partial class HistoryView : UserControl
{
    /// <summary>构造。</summary>
    public HistoryView()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Window.GetWindow(this)?.Close();
}
