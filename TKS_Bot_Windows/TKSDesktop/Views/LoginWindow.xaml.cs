using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.ViewModels;

namespace TKSDesktop.Views;

/// <summary>
/// 登录窗口（FR-W-AUTH-1..13 / EDGE-W-2 / EDGE-W-3）。
///
/// * 登录成功后：关掉本窗口并创建主窗口（主窗口负责建连与首屏）。
/// * 凭据经 DPAPI 持久化由 <c>IAuthService</c> 承担，本层不接触任何密钥材料。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly IServiceProvider _provider;
    private readonly LoginViewModel _viewModel;
    private bool _handedOff;

    /// <summary>构造（由 <c>WindowFactory</c> 调用）。</summary>
    public LoginWindow(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        InitializeComponent();
        _provider = provider;
        _viewModel = provider.GetRequiredService<LoginViewModel>();
        DataContext = _viewModel;

        _viewModel.LoginSucceeded += OnLoginSucceeded;
        _viewModel.CloseRequested += OnCloseRequested;
    }

    /// <summary>显示窗口并应用启动协调器注入的提示。</summary>
    public void ShowWindow(string? noticeKey)
    {
        _viewModel.ApplyNotice(noticeKey);

        if (IsVisible)
        {
            Activate();
            return;
        }

        Show();
        UsernameBox.Focus();
    }

    /// <summary>关闭窗口（登录成功切换主界面时调用）。</summary>
    public void CloseWindow()
    {
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未创建 / 已在关闭流程中：忽略。
        }
    }

    /// <summary>取消按钮文案（从视图模型读取，避免 XAML 硬编码 —— V-W-S8）。</summary>
    public string CancelText => I18n.T("common.cancel");

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // 密码只在内存中传递：不落盘、不进日志（FR-W-AUTH-3 / FR-W-SEC-2）。
        if (sender is PasswordBox box)
        {
            _viewModel.Password = box.Password;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWindow();

    private void OnLoginSucceeded(object? sender, EventArgs e)
    {
        if (_handedOff)
        {
            return;
        }

        _handedOff = true;

        // 主界面由窗口工厂创建；消息循环仍由启动协调器管理（不得在此 app.Run()）。
        WindowFactory.ShowMain(_provider, startHidden: false);
        _ = StartupCoordinator.StartBackgroundAsync(_provider);
        CloseWindow();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => CloseWindow();

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LoginSucceeded -= OnLoginSucceeded;
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);

        // 应用采用 OnExplicitShutdown。用户在登录页取消时没有托盘主界面可驻留，
        // 因此必须显式结束消息循环；登录成功切换主窗口时则继续运行。
        if (!_handedOff)
        {
            Application.Current?.Shutdown();
        }
    }
}
