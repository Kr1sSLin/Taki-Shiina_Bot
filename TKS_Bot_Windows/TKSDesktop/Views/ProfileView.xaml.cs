using System.Windows;
using System.Windows.Controls;
using TKSDesktop.ViewModels;

namespace TKSDesktop.Views;

/// <summary>
/// 个人中心视图（FR-W-LV-1..8 / FR-W-PT-1..7 / FR-W-MC-1..7）。
/// 代码隐藏只处理「补签日期点击」的事件转发，二次确认在 ViewModel 内完成（FR-W-MC-3）。
/// </summary>
public partial class ProfileView : UserControl
{
    /// <summary>构造。</summary>
    public ProfileView()
    {
        InitializeComponent();
    }
    private void MakeupDate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfileViewModel profile)
        {
            return;
        }

        if (sender is Button { Content: string date } && !string.IsNullOrWhiteSpace(date))
        {
            // ViewModel 内部先弹二次确认，确认后才调服务端（FR-W-MC-3）。
            profile.UseMakeupCardCommand.Execute(date);
        }
    }
}
