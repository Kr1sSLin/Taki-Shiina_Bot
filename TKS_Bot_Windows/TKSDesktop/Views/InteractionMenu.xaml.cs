using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.ViewModels;

namespace TKSDesktop.Views;

/// <summary>
/// 互动菜单浮层（FR-W-INT-1..9）。
/// 由 <c>MainWindow</c> 在右下角「+」被点击时显示（占位遮罩 + 浮层，非独立窗口）。
/// </summary>
public partial class InteractionMenu : UserControl
{
    private InteractionMenuViewModel? _viewModel;

    /// <summary>构造。</summary>
    public InteractionMenu()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>请求关闭浮层（送出成功 / 用户点击关闭）。</summary>
    public event EventHandler? CloseRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        Attach();
    }

    private void Attach()
    {
        if (DataContext is not InteractionMenuViewModel menu)
        {
            return;
        }

        if (ReferenceEquals(_viewModel, menu)) return;
        _viewModel = menu;
        menu.CloseRequested += OnCloseRequested;
    }

    private void Detach()
    {
        if (_viewModel is { } menu)
        {
            menu.CloseRequested -= OnCloseRequested;
        }

        _viewModel = null;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>加载物品列表（<c>GET /interaction/items</c>；置灰用服务端 affordable —— FR-W-INT-4）。</summary>
    public Task LoadAsync() => _viewModel?.LoadAsync() ?? Task.CompletedTask;

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: InteractionItemViewModel item } && _viewModel is { } menu)
        {
            if (menu.SendCommand.CanExecute(item)) menu.SendCommand.Execute(item);
        }
    }

    private void IconImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image) image.Visibility = Visibility.Collapsed;
    }
}
