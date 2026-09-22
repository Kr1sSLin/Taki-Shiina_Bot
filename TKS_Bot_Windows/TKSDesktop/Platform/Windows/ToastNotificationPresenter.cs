using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// Toast 通知呈现 + 托盘气泡降级（PRD §6.7 FR-W-NOTI-3/4/5/8/9、§14.3、EDGE-W-10）。
///
/// 实现要点：
/// <list type="bullet">
///   <item>**载荷**用 <c>CommunityToolkit.WinUI.Notifications 7.1.2</c> 的 <see cref="ToastContentBuilder"/>
///         生成 XML（该包在 <c>net8.0-windows</c> 下**只提供 XML 生成**，不含 <c>ToastNotificationManager</c>）；</item>
///   <item>**投递**走手写 WinRT ABI（<see cref="WinRt"/>）：<c>RoGetActivationFactory</c> +
///         <c>IToastNotificationManagerStatics::CreateToastNotifier(appId)</c> + <c>IToastNotifier::Show</c>。
///         ⚠️ 原因：本工程 <c>net8.0-windows</c> 且未引用 Windows SDK 契约程序集，
///         <c>Windows.UI.Notifications.*</c> 与 CsWinRT 投影在**编译期不可用**（见 M0 实测）；
///         接口 IID 与 vtable 槽位均已用探针工程逐项实测（报告见交接说明）；</item>
///   <item>FR-W-NOTI-3：同类通知**替换**而非堆叠 —— 设置 <c>Tag</c>=<see cref="SemanticNotificationIds.ToastTag"/>
///         与 <c>Group</c>=<see cref="SemanticNotificationIds.ToastGroup"/>；</item>
///   <item>FR-W-NOTI-4：带「回复」「忽略」按钮，参数含 <c>messageId</c>；</item>
///   <item>FR-W-NOTI-8：交互式输入框形态不支持时**降级为「仅唤起窗口」**，绝不让 Toast 生成失败；</item>
///   <item>EDGE-W-10 / FR-W-NOTI-9：Toast 不可用时降级为托盘气泡，并如实说明能力状态。</item>
/// </list>
/// </summary>
public sealed class ToastNotificationPresenter : INotificationPresenter, IDisposable
{
    /// <summary>开始菜单快捷方式（§14.3 第 2 步：Toast 能弹出的**硬前提**）。</summary>
    public const string StartMenuShortcutName = "TKS Desktop.lnk";

    /// <summary>Toast 动作参数值（回传给激活逻辑，勿改字面量）。</summary>
    public const string ActionOpen = "open";

    /// <summary>「回复」按钮的动作值（唤起窗口并聚焦输入框，FR-W-DSK-7）。</summary>
    public const string ActionReply = "reply";

    /// <summary>「忽略」按钮的动作值。</summary>
    public const string ActionDismiss = "dismiss";

    /// <summary>参数名（与 FR-W-NOTI-4 约定的 <c>action=open&amp;messageId=xxx</c> 一致）。</summary>
    public const string ParameterAction = "action";

    /// <summary>消息 id 参数名。</summary>
    public const string ParameterMessageId = "messageId";

    private readonly ITrayIcon? _trayFallback;
    private readonly string _aumid;
    private readonly string _startMenuShortcutPath;

    private IntPtr _notifier;
    private IntPtr _history;
    private bool _initialized;

    /// <summary>
    /// Toast 是否可用（EDGE-W-10 / FR-W-NOTI-9）：由初始化探测「AUMID 已声明 +
    /// 开始菜单快捷方式存在」两个前置条件后写入。
    /// </summary>
    private bool _toastAvailable;

    /// <summary>能力描述覆盖文本（自检/测试注入用）；为 <c>null</c> 时由探测结果生成。</summary>
    private string? _capabilityOverride;

    public ToastNotificationPresenter(
        ITrayIcon? trayFallback = null,
        string aumid = ProtocolConstants.Aumid,
        string? startMenuShortcutPath = null)
    {
        _trayFallback = trayFallback;
        _aumid = aumid;
        _startMenuShortcutPath = startMenuShortcutPath ?? DefaultShortcutPath();
    }

    /// <summary>用于自检断言：当前探测到的开始菜单快捷方式路径。</summary>
    public string StartMenuShortcutPath => _startMenuShortcutPath;

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-NOTI-9 的两个前置条件：
    /// ① AUMID 已在 <c>Program.Main</c> 通过 <c>SetCurrentProcessExplicitAppUserModelID</c> 声明（进程级，视为 true）；
    /// ② <c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\TKS Desktop.lnk</c> 存在（安装包创建）。
    /// 便携版不创建该快捷方式 → false → 降级托盘气泡。
    /// </remarks>
    public bool IsToastAvailable
    {
        get
        {
            if (!_initialized)
            {
                EnsureInitialized();
            }

            return _toastAvailable;
        }
    }

    /// <inheritdoc />
    /// <remarks>EDGE-W-10：如实展示当前能力（NFR-W-8），文案取自 i18n 资源键。</remarks>
    public string CapabilityDescription
    {
        get
        {
            if (_capabilityOverride is not null)
            {
                return _capabilityOverride;
            }

            if (IsToastAvailable)
            {
                // 能力可用：不额外提示（避免噪声），仅返回 aumid 以便设置页展示。
                return _aumid;
            }

            if (!File.Exists(_startMenuShortcutPath))
            {
                // 便携版未安装 → 给出「运行一次安装包」的建议（FR-W-NOTI-9）。
                return $"{I18n.T("notification.toast.unavailable")} · {I18n.T("notification.portableHint")}";
            }

            return I18n.T("notification.toast.unavailable");
        }
    }

    /// <inheritdoc />
    public void Present(SemanticNotificationId id, string title, string body, string? messageId = null)
    {
        if (!IsToastAvailable)
        {
            // EDGE-W-10：降级为托盘气泡（NIF_INFO）。
            _trayFallback?.ShowBalloon(title, body);
            return;
        }

        try
        {
            ShowToast(id, title, body, messageId);
        }
        catch (Exception ex)
        {
            // FR-W-NOTI-8：绝不让通知层抛错打断业务；记录状态并降级。
            _capabilityOverride = $"{I18n.T("notification.toast.unavailable")} ({NativeMethods.Describe(ex)})";
            _toastAvailable = false;
            _trayFallback?.ShowBalloon(title, body);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-NOTI-5：应用回到前台时清除**消息类**（Chat / Greeting）通知；
    /// 提醒与错误类**不清**（它们在通知中心仍有价值）。
    ///
    /// ⚠️ 全部语义 ID 共用一个覆盖组 <see cref="SemanticNotificationIds.ToastGroup"/>，
    /// 因此按组删除会误删提醒/错误 —— 必须**按 Tag 逐个精确删除**。
    /// </remarks>
    public void ClearMessageNotifications()
    {
        if (!IsToastAvailable || _history == IntPtr.Zero)
        {
            return;
        }

        foreach (var id in new[] { SemanticNotificationId.Chat, SemanticNotificationId.Greeting })
        {
            try
            {
                WinRt.RemoveFromHistoryByTag(_history, id.ToastTag(), SemanticNotificationIds.ToastGroup, _aumid);
            }
            catch (Exception)
            {
                // 清除失败不影响业务（通知中心残留只是观感问题）。
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// FR-W-DSK-10：读取系统「专注助手 / 勿扰」。
    /// <c>QUNS_NOT_PRESENT</c> / <c>QUNS_BUSY</c> / <c>QUNS_RUNNING_D3D_FULL_SCREEN</c> /
    /// <c>QUNS_PRESENTATION_MODE</c> 视为**抑制**（抑制 Toast 但仍正常收消息）。
    /// </remarks>
    public bool IsSystemDoNotDisturbActive()
    {
        try
        {
            // S_OK 之外（API 不可用）按「不抑制」处理：宁可多弹一次也不静默吞消息。
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0)
            {
                return false;
            }

            return state is NativeMethods.QunsNotPresent
                or NativeMethods.QunsBusy
                or NativeMethods.QunsRunningD3dFullScreen
                or NativeMethods.QunsPresentationMode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>释放 WinRT 对象引用（幂等）。</summary>
    public void Dispose()
    {
        WinRt.Release(ref _notifier);
        WinRt.Release(ref _history);
        _initialized = false;
        _toastAvailable = false;
        GC.SuppressFinalize(this);
    }

    private static string DefaultShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "Start Menu",
        "Programs",
        "TKS Desktop",
        StartMenuShortcutName);

    private void EnsureInitialized()
    {
        _initialized = true;

        // ① AUMID：进程级声明失败也不阻断（降级路径已就绪）。
        try
        {
            _ = NativeMethods.SetCurrentProcessExplicitAppUserModelID(_aumid);
        }
        catch (Exception)
        {
            // 忽略：下面仍会尝试 CreateToastNotifier。
        }

        // ② 开始菜单快捷方式：不存在则**直接**判定不可用（EDGE-W-10 前置条件检测）。
        if (!File.Exists(_startMenuShortcutPath))
        {
            _toastAvailable = false;
            return;
        }

        try
        {
            if (!WinRt.EnsureInitialized())
            {
                _toastAvailable = false;
                return;
            }

            _notifier = WinRt.CreateNotifier(_aumid);
            if (_notifier == IntPtr.Zero)
            {
                _toastAvailable = false;
                return;
            }

            // 通知中心（History）用于 FR-W-NOTI-5 的精确清除；不可用不影响发送。
            _history = WinRt.GetHistory();

            // 系统策略禁用通知时 CreateToastNotifier 成功但 Setting != Enabled。
            var setting = WinRt.GetNotifierSetting(_notifier);
            _toastAvailable = setting == WinRt.SettingEnabled;
        }
        catch (Exception ex)
        {
            _capabilityOverride = I18n.T("notification.toast.unavailable") + $" ({NativeMethods.Describe(ex)})";
            _toastAvailable = false;
            WinRt.Release(ref _notifier);
            WinRt.Release(ref _history);
        }
    }

    /// <summary>
    /// 生成并投递一条 Toast。
    /// FR-W-NOTI-8 的降级策略：先尝试「回复 + 忽略」按钮形态；
    /// 若含交互式输入框的形态构造失败，则回退为「仅唤起窗口」的纯按钮形态，**绝不**让通知失败。
    /// </summary>
    private void ShowToast(SemanticNotificationId id, string title, string body, string? messageId)
    {
        var tag = id.ToastTag();
        var group = SemanticNotificationIds.ToastGroup;
        var launch = NotificationActivation.BuildUri(ActionOpen, messageId);

        var xml = BuildXml(title, body, launch, messageId, includeReplyButton: true)
            ?? BuildXml(title, body, launch, messageId, includeReplyButton: false)
            ?? throw new InvalidOperationException("toast payload generation failed for both interactive and plain forms");

        WinRt.Show(_notifier!, xml, tag, group);
    }

    /// <summary>
    /// 构造 Toast XML；失败返回 <c>null</c> 让调用方降级（FR-W-NOTI-8）。
    /// </summary>
    private static string? BuildXml(
        string title, string body, string launch, string? messageId, bool includeReplyButton)
    {
        try
        {
            var builder = new ToastContentBuilder();

            builder.AddText(title);
            builder.AddText(body);

            if (includeReplyButton)
            {
                // FR-W-DSK-7：「回复」「忽略」两个按钮。
                // ⚠️ 交互式**文本输入框**回复（ToastTextBox）在未安装/未注册 COM 激活器的经典 Win32 应用上
                //    可能构造或激活失败；其上层的 try/catch 会把异常转为「仅唤起窗口」的纯按钮形态（FR-W-NOTI-8）。
                builder.AddButton(new ToastButton()
                    .SetContent(I18n.T("notification.action.reply"))
                    .SetProtocolActivation(new Uri(NotificationActivation.BuildUri(ActionReply, messageId))));

                builder.AddButton(new ToastButton()
                    .SetContent(I18n.T("notification.action.ignore"))
                    .AddArgument(ParameterAction, ActionDismiss)
                    .SetDismissActivation());
            }

            // launch 参数通过 AddArgument 写入；这里显式校验生成结果非空。
            var content = builder.GetToastContent();
            content.ActivationType = ToastActivationType.Protocol;
            content.Launch = launch;
            var xml = ((INotificationContent)content).GetContent();
            return string.IsNullOrWhiteSpace(xml) ? null : xml;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>构造 <c>action=...&amp;messageId=...</c> 形式的参数串（供文档与自检对齐）。</summary>
    private static string BuildArguments(string action, string? messageId)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(ParameterAction).Append('=').Append(action);

        if (!string.IsNullOrEmpty(messageId))
        {
            builder.Append('&').Append(ParameterMessageId).Append('=').Append(messageId);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 手写 WinRT ABI（IID 与 vtable 槽位均为实测值）。
    ///
    /// ⚠️ 仅在 <c>net8.0-windows</c> 无 CsWinRT 投影时使用；
    ///    所有槽位编号的推导依据见本类注释与交接报告，**不得**凭猜测修改。
    /// </summary>
    private static class WinRt
    {
        internal const int SettingEnabled = 0;

        private static readonly Guid IidToastNotificationManagerStatics =
            new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

        private static readonly Guid IidToastNotificationManagerStatics2 =
            new("7AB93C52-0E48-4750-BA9D-1A4113981847");

        private static readonly Guid IidToastNotificationFactory =
            new("04124B20-82C6-4229-B109-FD9ED4662B53");

        private static readonly Guid IidXmlDocumentIo =
            new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

        private static readonly Guid IidToastNotification2 =
            new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");

        private static readonly Guid IidToastNotifier =
            new("75927B93-03F3-41EC-91D3-6E5BAC1B38E7");

        private static IntPtr _mgrStatics;
        private static IntPtr _mgrStatics2;
        private static IntPtr _toastFactory;

        /// <summary>三个 IInspectable 前缀槽位（QueryInterface/AddRef/Release）。</summary>
        private const int InspectableSlots = 6;

        /// <summary>初始化 WinRT（幂等）。失败返回 <c>false</c> 而非抛错。</summary>
        internal static bool EnsureInitialized()
        {
            lock (typeof(WinRt))
            {
                if (_mgrStatics != IntPtr.Zero && _toastFactory != IntPtr.Zero)
                {
                    return true;
                }

                try
                {
                    // RO_INIT_MULTITHREADED = 1；S_FALSE(0x1) 与 RPC_E_CHANGED_MODE 均视为「已初始化」。
                    var hr = NativeMethods.RoInitialize(1);
                    if (hr != 0 && hr != 0x1 && hr != unchecked((int)0x80010106))
                    {
                        return false;
                    }

                    _winRtInitializedStatic = true;

                    _mgrStatics = ActivationFactory(
                        "Windows.UI.Notifications.ToastNotificationManager", IidToastNotificationManagerStatics);
                    _mgrStatics2 = ActivationFactory(
                        "Windows.UI.Notifications.ToastNotificationManager", IidToastNotificationManagerStatics2);
                    _toastFactory = ActivationFactory(
                        "Windows.UI.Notifications.ToastNotification", IidToastNotificationFactory);

                    return _mgrStatics != IntPtr.Zero && _toastFactory != IntPtr.Zero;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>镜像「WinRT 已初始化」位，供设置页/自检诊断。</summary>
        private static bool _winRtInitializedStatic;

        /// <summary>进程内是否已成功初始化 WinRT（诊断用）。</summary>
        internal static bool IsInitialized => _winRtInitializedStatic;

        /// <summary>
        /// <c>IToastNotificationManagerStatics::CreateToastNotifier(HSTRING appId)</c>。
        /// ⚠️ 必须用**带 appId 的重载（槽位 7）**：无参重载（槽位 6）要求 AUMID 已安装，
        ///    在便携/未注册场景下返回 <c>ELEMENT_NOT_FOUND (0x80070490)</c>（已实测）。
        /// </summary>
        internal static IntPtr CreateNotifier(string appId)
        {
            if (_mgrStatics == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var h = NativeMethods.CreateHString(appId);
            if (h == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var hr = Invoke1Out(_mgrStatics, InspectableSlots + 1, h, out var notifier);
                if (hr != 0 || notifier == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                return QueryInterface(notifier, IidToastNotifier);
            }
            finally
            {
                _ = NativeMethods.WindowsDeleteString(h);
            }
        }

        /// <summary>读取 <c>IToastNotifier::get_Setting</c>（槽位 8）。0 = enabled。</summary>
        internal static int GetNotifierSetting(IntPtr notifier)
        {
            if (notifier == IntPtr.Zero)
            {
                return -1;
            }

            var hr = Invoke0OutInt(notifier, InspectableSlots + 2, out var setting);
            return hr == 0 ? setting : -1;
        }

        /// <summary>
        /// <c>IToastNotificationManagerStatics2::get_History</c> → QI 到
        /// <c>IToastNotificationHistory</c>（用于 FR-W-NOTI-5 的精确清除）。
        /// </summary>
        internal static IntPtr GetHistory()
        {
            if (_mgrStatics2 == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var hr = Invoke0Out(_mgrStatics2, InspectableSlots, out var history);
                return hr == 0 ? history : IntPtr.Zero;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 投递：XmlDocument.LoadXml → ToastNotificationFactory.CreateToastNotification →
        /// <c>IToastNotification2</c> 设置 Tag/Group → <c>IToastNotifier::Show</c>。
        /// </summary>
        internal static void Show(IntPtr notifier, string xml, string tag, string group)
        {
            if (notifier == IntPtr.Zero)
            {
                throw new InvalidOperationException("toast notifier is not available");
            }

            var xmlDocument = CreateXmlDocument(xml);
            var toast = CreateToast(xmlDocument);
            if (toast == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateToastNotification returned null");
            }

            var toast2 = QueryInterface(toast, IidToastNotification2);
            if (toast2 != IntPtr.Zero)
            {
                // FR-W-NOTI-3：同类通知替换而非堆叠。
                SetHStringProperty(toast2, InspectableSlots, tag);       // put_Tag
                SetHStringProperty(toast2, InspectableSlots + 2, group); // put_Group
                Release(ref toast2);
            }

            var hr = Invoke1(notifier, InspectableSlots, toast);
            Release(ref toast);

            if (hr != 0)
            {
                throw new InvalidOperationException($"IToastNotifier::Show failed (0x{hr:X8})");
            }
        }

        /// <summary>
        /// FR-W-NOTI-5 的精确清除：<c>IToastNotificationHistory::Remove(tag, group, appId)</c>
        /// （槽位 <see cref="InspectableSlots"/> + 2）。
        /// ⚠️ **不得**改用 <c>RemoveGroup(group)</c>：本端所有语义 ID 共用一个覆盖组，
        ///    按组删除会连提醒/错误类一起清掉。
        /// </summary>
        internal static void RemoveFromHistoryByTag(IntPtr history, string tag, string group, string appId)
        {
            if (history == IntPtr.Zero)
            {
                return;
            }

            var hTag = NativeMethods.CreateHString(tag);
            var hGroup = NativeMethods.CreateHString(group);
            var hApp = NativeMethods.CreateHString(appId);
            try
            {
                _ = Invoke3(history, InspectableSlots + 2, hTag, hGroup, hApp);
            }
            finally
            {
                _ = NativeMethods.WindowsDeleteString(hTag);
                _ = NativeMethods.WindowsDeleteString(hGroup);
                _ = NativeMethods.WindowsDeleteString(hApp);
            }
        }

        private static IntPtr CreateXmlDocument(string xml)
        {
            var classId = NativeMethods.CreateHString("Windows.Data.Xml.Dom.XmlDocument");
            var h = NativeMethods.CreateHString(xml);
            try
            {
                var hr = NativeMethods.RoActivateInstance(classId, out var instance);
                if (hr != 0 || instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"RoActivateInstance(XmlDocument) failed (0x{hr:X8})");
                }

                var io = QueryInterface(instance, IidXmlDocumentIo);
                Release(ref instance);

                if (io == IntPtr.Zero)
                {
                    throw new InvalidOperationException("IXmlDocumentIO is not available on XmlDocument");
                }

                // IXmlDocumentIO::LoadXml(HSTRING) 位于槽位 6。
                var loadHr = Invoke1(io, InspectableSlots, h);
                if (loadHr != 0)
                {
                    Release(ref io);
                    throw new InvalidOperationException($"IXmlDocumentIO.LoadXml failed (0x{loadHr:X8})");
                }

                return io;
            }
            finally
            {
                _ = NativeMethods.WindowsDeleteString(classId);
                _ = NativeMethods.WindowsDeleteString(h);
            }
        }

        private static IntPtr CreateToast(IntPtr xmlDocument)
        {
            if (_toastFactory == IntPtr.Zero || xmlDocument == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var hr = Invoke1Out(_toastFactory, InspectableSlots, xmlDocument, out var toast);
            return hr == 0 ? toast : IntPtr.Zero;
        }

        private static IntPtr ActivationFactory(string className, Guid iid)
        {
            var classId = NativeMethods.CreateHString(className);
            if (classId == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                var local = iid;
                var hr = NativeMethods.RoGetActivationFactory(classId, ref local, out var factory);
                return hr == 0 ? factory : IntPtr.Zero;
            }
            finally
            {
                _ = NativeMethods.WindowsDeleteString(classId);
            }
        }

        private static IntPtr QueryInterface(IntPtr instance, Guid iid)
        {
            if (instance == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), 0));
            var local = iid;
            return fn(instance, ref local, out var result) == 0 ? result : IntPtr.Zero;
        }

        private static void SetHStringProperty(IntPtr instance, int slot, string value)
        {
            var h = NativeMethods.CreateHString(value);
            try
            {
                _ = Invoke1(instance, slot, h);
            }
            finally
            {
                _ = NativeMethods.WindowsDeleteString(h);
            }
        }

        private static int Invoke1(IntPtr instance, int slot, IntPtr arg0) =>
            Marshal.GetDelegateForFunctionPointer<Vtbl1>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size))(instance, arg0);

        private static int Invoke3(IntPtr instance, int slot, IntPtr arg0, IntPtr arg1, IntPtr arg2) =>
            Marshal.GetDelegateForFunctionPointer<Vtbl3>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size))(instance, arg0, arg1, arg2);

        private static int Invoke0Out(IntPtr instance, int slot, out IntPtr result) =>
            Marshal.GetDelegateForFunctionPointer<Vtbl0Out>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size))(instance, out result);

        private static int Invoke1Out(IntPtr instance, int slot, IntPtr arg0, out IntPtr result) =>
            Marshal.GetDelegateForFunctionPointer<Vtbl1Out>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size))(instance, arg0, out result);

        private static int Invoke0OutInt(IntPtr instance, int slot, out int result) =>
            Marshal.GetDelegateForFunctionPointer<Vtbl0OutInt>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size))(instance, out result);

        /// <summary>Release（槽位 2）。仅递减引用计数，不释放静态工厂缓存。</summary>
        internal static void Release(ref IntPtr instance)
        {
            if (instance == IntPtr.Zero)
            {
                return;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), 2 * IntPtr.Size));
            _ = fn(instance);
            instance = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint ReleaseFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Vtbl1(IntPtr self, IntPtr arg0);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Vtbl3(IntPtr self, IntPtr arg0, IntPtr arg1, IntPtr arg2);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Vtbl0Out(IntPtr self, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Vtbl1Out(IntPtr self, IntPtr arg0, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Vtbl0OutInt(IntPtr self, out int result);
    }
}
