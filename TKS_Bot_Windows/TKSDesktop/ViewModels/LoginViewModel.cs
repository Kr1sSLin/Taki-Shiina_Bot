using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>
/// 登录页视图模型（FR-W-AUTH-1..13 / EDGE-W-2 / EDGE-W-3）。
///
/// <list type="bullet">
///   <item>失败原因按 <see cref="LoginFailureReason"/> 分类给出不同文案（未知原因回中性兜底）；</item>
///   <item>网络故障**必须保留凭据、不踢人**（EDGE-W-2）：只提示可重试，不清任何本地状态；</item>
///   <item>凭据无法安全落盘时如实展示（<c>login.credentialNotPersisted</c>），**不降级为明文**；</item>
///   <item>登录成功后由 <c>Views.WindowFactory</c> 切换到主界面（本类只发事件，不引用 WPF）。</item>
/// </list>
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;

    /// <summary>构造。</summary>
    public LoginViewModel(IAuthService auth, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(settings);
        _auth = auth;
        _settings = settings;

        // FR-W-AUTH-9：只回填上次用户名，**不记密码**。
        _username = _settings.Current.LastUsername ?? string.Empty;
    }

    /// <summary>登录成功（由窗口订阅后切换到主界面）。</summary>
    public event EventHandler? LoginSucceeded;

    /// <summary>请求关闭登录窗口（窗口订阅）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>用户名。</summary>
    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>密码（只在内存中；不落盘、不进日志）。</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>是否正在登录。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>提示文案（启动协调器注入的 noticeKey 或失败原因）。</summary>
    [ObservableProperty]
    private string _notice = string.Empty;

    /// <summary>提示是否可用（空文案时不占位）。</summary>
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>是否可提交（空用户名 / 空密码 / 忙碌时禁用）。</summary>
    public bool CanSubmit => !IsBusy && !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);

    /* ---- 静态 UI 文案（XAML 只绑定，不写死中文 —— V-W-S8） ---- */

    /// <summary>窗口标题。</summary>
    public string TitleText => I18n.T("login.title");

    /// <summary>用户名标签。</summary>
    public string UsernameLabel => I18n.T("login.username");

    /// <summary>密码标签。</summary>
    public string PasswordLabel => I18n.T("login.password");

    /// <summary>提交按钮文案（繁忙时切换为「登录中…」）。</summary>
    public string SubmitText => I18n.T(IsBusy ? "login.submitting" : "login.submit");

    /// <summary>产品名。</summary>
    public string AppName => I18n.T("app.name");

    /// <summary>展示启动协调器注入的提示（登录过期 / 凭据损坏 / 空闲超期 等）。</summary>
    public void ApplyNotice(string? noticeKey)
    {
        Notice = string.IsNullOrWhiteSpace(noticeKey) ? string.Empty : I18n.T(noticeKey);
    }

    /// <summary>执行登录（FR-W-AUTH-1/2/3/8）。</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Notice = string.Empty;

        try
        {
            var outcome = await _auth.LoginAsync(Username, Password).ConfigureAwait(true);

            if (!outcome.Success)
            {
                Notice = Describe(outcome);
                return;
            }

            // 凭据无法安全落盘：如实告知，**不静默**（EDGE-W-11 分支 ①）。
            if (_auth.CredentialStatus is { IsEncryptionAvailable: false })
            {
                Notice = I18n.T("login.credentialNotPersisted");
            }

            await PersistLastUsernameAsync().ConfigureAwait(true);
            Password = string.Empty;
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>关闭窗口。</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 失败原因 → 文案。⚠️ `NetworkError` 只提示可重试，**不含任何「请重新登录」类表述**
    /// （否则用户会以为凭据失效；EDGE-W-2）。
    /// </summary>
    private static string Describe(Core.Services.LoginOutcome outcome)
    {
        var key = outcome.Reason switch
        {
            LoginFailureReason.InvalidCredentials => "error.api.40101",
            LoginFailureReason.DeviceNotAllowed => "error.api.40301",
            LoginFailureReason.DeviceLimitExceeded => "error.api.40302",
            LoginFailureReason.ServerError => "error.api.50301",
            LoginFailureReason.NetworkError => "error.client.CONNECTION_LOST",
            LoginFailureReason.Unknown => outcome.I18nKey,
            _ => outcome.I18nKey,
        };

        var text = I18n.T(key);
        return string.IsNullOrWhiteSpace(text) ? I18n.T("login.failed") : text;
    }

    private Task PersistLastUsernameAsync()
    {
        var current = _settings.Current;
        current.LastUsername = Username;

        // 保存失败不阻断登录（用户名仅作回填便利）。
        return _settings.SaveAsync(current);
    }

    partial void OnUsernameChanged(string value) => RefreshCanSubmit();

    partial void OnPasswordChanged(string value) => RefreshCanSubmit();

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCanSubmit();
        OnPropertyChanged(nameof(SubmitText));
    }

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    private void RefreshCanSubmit() => OnPropertyChanged(nameof(CanSubmit));
}
