using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKSDesktop.App;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.ViewModels;

/// <summary>
/// 聊天页视图模型（FR-W-CHAT-1..18 / FR-W-CONN-5 / FR-W-CONN-8 / FR-W-IMG-1/3/7/8/9 /
/// FR-W-PROG-4 / FR-W-ARCH-3）。
///
/// 契约要点：
/// <list type="bullet">
///   <item>首屏只加载 <see cref="ProtocolConstants.LocalFirstPageSize"/>（50）条，向上滚动分页；</item>
///   <item>同期内相邻消息间隔 ≤ 1 分钟只显示一条时间（FR-W-CHAT-13），跨日由 C-5 分隔线承担；</item>
///   <item>C-5 分隔线（<c>IsHistorySeparator</c>）单独渲染为细线，**不得**当普通气泡；</item>
///   <item>互动气泡（<c>InteractionItemName</c> / <c>Icon</c> 非空）复用同一气泡模板（FR-W-SYNC-11 / FR-W-INT-6）；</item>
///   <item>连接状态断开时提供「重新连接」（FR-W-CONN-5）；</item>
///   <item>离线缓存态时整体禁用输入与互动入口（FR-W-PROG-4）；</item>
///   <item>所有 <see cref="ObservableCollection{T}"/> 变更**必须**经 <see cref="IUiDispatcher"/>（FR-W-ARCH-3）。</item>
/// </list>
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IChatService _chat;
    private readonly IMediaService _media;
    private readonly ISettingsService _settings;
    private readonly IGamificationService _gamification;
    private readonly ILocalHistoryReader _history;
    private readonly IUserPrompt _prompt;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISyncService _sync;

    private readonly Dictionary<string, MessageItemViewModel> _byId = new(StringComparer.Ordinal);
    private readonly List<string> _unreadIncoming = [];

    private bool _disposed;
    private int _loadedCount;

    /// <summary>构造。</summary>
    public ChatViewModel(
        IChatService chat,
        IMediaService media,
        ISettingsService settings,
        IGamificationService gamification,
        ILocalHistoryReader history,
        IUserPrompt prompt,
        IUiDispatcher dispatcher,
        ISyncService sync)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(gamification);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _chat = chat;
        _media = media;
        _settings = settings;
        _gamification = gamification;
        _history = history;
        _prompt = prompt;
        _dispatcher = dispatcher;
        _sync = sync;
        _sync.ChatSynchronized += OnChatSynchronized;
        _sync.SyncFailed += OnSyncFailed;

        ConnectionStatusText = I18n.T("conn.status.connecting");
        ApplyConnection(_chat.State);
        ApplyProgress(_gamification.Current);

        _chat.ConnectionChanged += OnConnectionChanged;
        _chat.TypingChanged += OnTypingChanged;
        _chat.MessageChanged += OnMessageChanged;
        _chat.MessageRemoved += OnMessageRemoved;
        _chat.ConversationCleared += OnConversationCleared;
        _chat.NoticeRaised += OnNoticeRaised;
        _chat.ScrollToMessageRequested += OnScrollToMessageRequested;
        _gamification.ProgressChanged += OnProgressChanged;

        FillEmojiPalette();
    }

    /* ---- 事件（供 View 层响应，避免 ViewModel 引用 WPF） ---- */

    /// <summary>请求把消息列表滚动到末尾。</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>请求把消息列表滚动到指定消息（点击通知 —— FR-W-NOTI-4）。</summary>
    public event EventHandler<string>? ScrollToMessageRequested;

    /// <summary>请求弹出互动浮层（右下角「+」）（FR-W-INT-1）。</summary>
    public event EventHandler? InteractionMenuRequested;

    /// <summary>请求退出到托盘（Esc —— FR-W-DSK-6）。</summary>
    public event EventHandler? HideToTrayRequested;

    /* ---- 消息列表 ---- */

    /// <summary>消息（含 C-5 分隔线项）。</summary>
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>本地聊天记录搜索结果（最多 100 条）。</summary>
    public ObservableCollection<ChatSearchResult> SearchResults { get; } = [];

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _searchTerm = string.Empty;

    [ObservableProperty]
    private string _searchStatusText = string.Empty;

    [ObservableProperty]
    private bool _isSearchBusy;

    private int _searchGeneration;

    /// <summary>输入框正文。</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>草稿附件缩略图路径（输入框上方，可单张移除 —— FR-W-IMG-3）。</summary>
    public ObservableCollection<string> DraftThumbnails { get; } = [];

    /// <summary>顶部状态条文案（FR-W-CHAT-14）。</summary>
    [ObservableProperty]
    private string _typingText = string.Empty;

    /// <summary>是否显示状态条。</summary>
    [ObservableProperty]
    private bool _isTypingVisible;

    /// <summary>连接状态文案（FR-W-CONN-5）。</summary>
    [ObservableProperty]
    private string _connectionStatusText = string.Empty;

    /// <summary>是否断开（显示「重新连接」按钮）。</summary>
    [ObservableProperty]
    private bool _isDisconnected;

    /// <summary>降级态（重连达上限 —— FR-W-CHAT-9）。</summary>
    [ObservableProperty]
    private bool _isDegraded;

    /// <summary>轻量提示（NoticeRaised / 图片错误等）。</summary>
    [ObservableProperty]
    private string _noticeText = string.Empty;

    /// <summary>是否显示轻量提示。</summary>
    [ObservableProperty]
    private bool _isNoticeVisible;

    /// <summary>是否正在发送（发送期间禁用重复提交）。</summary>
    [ObservableProperty]
    private bool _isSending;

    /// <summary>是否可向上加载更早的消息（还有更多）。</summary>
    [ObservableProperty]
    private bool _canLoadMore;

    /// <summary>是否为离线缓存态（FR-W-PROG-4：互动菜单整体禁用）。</summary>
    [ObservableProperty]
    private bool _isOfflineCache;

    /// <summary>离线数据提示文案（<c>common.offlineData</c>）。</summary>
    [ObservableProperty]
    private string _offlineNoticeText = string.Empty;

    /// <summary>当前积分余额（互动菜单与输入条展示）。</summary>
    [ObservableProperty]
    private int _pointsBalance;

    /// <summary>当前等级称号。</summary>
    [ObservableProperty]
    private string _levelDisplayName = string.Empty;

    /// <summary>Bot 通知未读数（历史入口红点 —— FR-W-HIS-1）。</summary>
    [ObservableProperty]
    private int _unreadNotificationCount;

    /* ---- 静态 UI 文案（XAML 只绑定 —— V-W-S8） ---- */

    /// <summary>输入占位文案（随发送键设置变化，FR-W-SET-8）。</summary>
    public string InputPlaceholder => I18n.T("chat.input.placeholder");

    /// <summary>发送按钮文案。</summary>
    public string SendText => I18n.T("chat.send");

    /// <summary>「重新连接」按钮文案。</summary>
    public string ReconnectText => I18n.T("conn.reconnect");

    /// <summary>「加载更早的消息」按钮文案。</summary>
    public string LoadMoreText => I18n.T("chat.loadMore");

    /// <summary>「添加图片」按钮文案。</summary>
    public string AddImageText => I18n.T("image.add");

    /// <summary>「送出互动」入口文案。</summary>
    public string InteractionText => I18n.T("profile.interaction");

    /// <summary>历史与记忆入口文案。</summary>
    public string HistoryText => I18n.T("history.title");

    /// <summary>个人中心入口文案。</summary>
    public string ProfileText => I18n.T("profile.title");

    /// <summary>提醒入口文案。</summary>
    public string RemindersText => I18n.T("reminder.title");

    /// <summary>设置入口文案。</summary>
    public string SettingsText => I18n.T("settings.title");

    /// <summary>搜索入口文案。</summary>
    public string SearchText => I18n.T("chat.search.placeholder");

    public string SearchButtonText => I18n.T("chat.search.action");

    public string CloseSearchText => I18n.T("common.close");

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        _searchGeneration++;
        IsSearchBusy = false;
    }

    [RelayCommand]
    private async Task SearchMessagesAsync()
    {
        var term = SearchTerm.Trim();
        var generation = ++_searchGeneration;
        if (term.Length == 0)
        {
            await _dispatcher.InvokeAsync(SearchResults.Clear).ConfigureAwait(true);
            SearchStatusText = I18n.T("chat.search.tooShort");
            return;
        }

        IsSearchBusy = true;
        SearchStatusText = string.Empty;
        try
        {
            var messages = await _chat.SearchAsync(term, 100).ConfigureAwait(true);
            if (generation != _searchGeneration)
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                SearchResults.Clear();
                foreach (var message in messages)
                {
                    SearchResults.Add(ChatSearchResult.From(message, term));
                }
            }).ConfigureAwait(true);
            SearchStatusText = messages.Count == 0
                ? I18n.T("chat.search.noResult")
                : I18n.T("chat.search.count", messages.Count);
        }
        catch
        {
            if (generation == _searchGeneration)
            {
                SearchStatusText = I18n.T("chat.search.failed");
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                IsSearchBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task JumpToSearchResultAsync(ChatSearchResult? result)
    {
        if (result is null)
        {
            return;
        }

        await LocateMessageAsync(result.MessageId).ConfigureAwait(true);
        CloseSearch();
    }

    /// <summary>清空本地会话入口文案。</summary>
    public string ClearLocalText => I18n.T("settings.clearLocal");

    /// <summary>联系人卡片标题（<c>app.name</c>）。</summary>
    public string ContactName => I18n.T("app.name");

    /// <summary>输入条提示：当前发送键语义（Enter / Ctrl+Enter）。</summary>
    public string SendKeyHintText => I18n.T(
        string.Equals(_settings.Current.SendKey, "ctrl+enter", StringComparison.Ordinal)
            ? "settings.sendKey.ctrlenter"
            : "settings.sendKey.enter");

    /// <summary>未读通知文案（<c>history.unread</c>）。</summary>
    public string UnreadText => I18n.T("history.unread", UnreadNotificationCount);

    /// <summary>是否允许输入（离线缓存态或正在发送时禁用）。</summary>
    /// <summary>是否允许输入（离线缓存态禁用 —— FR-W-PROG-4）。</summary>
    public bool CanInteract => !IsOfflineCache;

    /// <summary>重发按钮文案。</summary>
    public string RetryText => I18n.T("common.retry");

    /// <summary>缩略图单张移除按钮的自动化名称。</summary>
    public string RemoveDraftText => I18n.T("common.delete");

    /// <summary>降级模式提示（<c>chat.degraded.title</c> —— FR-W-CHAT-9）。</summary>
    public string DegradedNoticeText => I18n.T("chat.degraded.title");

    public string FallbackSendText => I18n.T("chat.degraded.send");

    public string FallbackNoticeText => I18n.T("chat.degraded.notice");

    [RelayCommand]
    private async Task SendFallbackAsync()
    {
        if (!IsDegraded || IsSending || string.IsNullOrWhiteSpace(InputText))
        {
            return;
        }

        if (_media.Drafts.Count > 0)
        {
            ShowNotice(I18n.T("chat.degraded.textOnly"));
            return;
        }

        IsSending = true;
        try
        {
            var result = await _chat.SendFallbackAsync(Guid.NewGuid().ToString("D"), InputText).ConfigureAwait(true);
            if (result.Success)
            {
                InputText = string.Empty;
            }
            else
            {
                ShowNotice(I18n.T(result.I18nKey ?? "error.unknown"));
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Emoji 面板开关按钮文案。</summary>
    public string EmojiButtonText => I18n.T("emoji.open");

    public IReadOnlyList<EmojiGroup> EmojiGroups { get; } = EmojiGroup.All;

    /// <summary>是否有草稿附件（决定缩略图条显隐 —— FR-W-IMG-3）。</summary>
    public bool HasDrafts => DraftThumbnails.Count > 0;

    /// <summary>当前发送键是否为 Ctrl+Enter（FR-W-SET-8）。</summary>
    public bool IsSendKeyCtrlEnter =>
        string.Equals(_settings.Current.SendKey, "ctrl+enter", StringComparison.Ordinal);

    /// <summary>Emoji 面板是否展开（**展开时占位推起内容**，非浮层 —— FR-W-UI-5）。</summary>
    [ObservableProperty]
    private bool _isEmojiPanelOpen;

    /// <summary>当前 Emoji 分类。</summary>
    [ObservableProperty]
    private string _emojiCategory = EmojiCategorySmileysKey;

    /// <summary>当前分类的 emoji 平铺集合。</summary>
    public ObservableCollection<string> EmojiPalette { get; } = [];

    /* ---- Emoji 分类标签（i18n 与分类键分离：标签走 I18n，键不落 UI 文案） ---- */

    /// <summary>`smileys` 分类键。</summary>
    public const string EmojiCategorySmileysKey = "smileys";

    /// <summary>`gestures` 分类键。</summary>
    public const string EmojiCategoryGesturesKey = "gestures";

    /// <summary>`faces` 分类键。</summary>
    public const string EmojiCategoryFacesKey = "faces";

    /// <summary>`food` 分类键。</summary>
    public const string EmojiCategoryFoodKey = "food";

    /// <summary>表情分类标签。</summary>
    public string EmojiCategorySmileys => I18n.T("emoji.smileys");

    /// <summary>手势分类标签。</summary>
    public string EmojiCategoryGestures => I18n.T("emoji.gestures");

    /// <summary>面孔分类标签。</summary>
    public string EmojiCategoryFaces => I18n.T("emoji.animals");

    /// <summary>食物分类标签。</summary>
    public string EmojiCategoryFood => I18n.T("emoji.food");

    /* ---- 生命周期 ---- */

    /// <summary>
    /// 窗口首次显示时调用：首屏本地加载 + 恢复草稿附件 + 刷新未读数。
    /// ⚠️ 不做网络请求（首屏必须是本地数据，FR-W-CHAT-16）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadInitialPageAsync(ct).ConfigureAwait(true);
        await RestoreDraftsAsync(ct).ConfigureAwait(true);
        await RefreshUnreadAsync(ct).ConfigureAwait(true);

        if (Messages.Count > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>向上滚动分页：再取一页更早的消息（页大小 = <see cref="ProtocolConstants.LocalFirstPageSize"/>）。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!CanLoadMore || _loadedCount >= int.MaxValue / 4)
        {
            return;
        }

        var page = await _history
            .ListRecentMessagesAsync(ProtocolConstants.LocalFirstPageSize, _loadedCount)
            .ConfigureAwait(true);

        if (page.Count == 0)
        {
            CanLoadMore = false;
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            // 旧页插到队首，且保持时间升序。
            var index = 0;
            foreach (var view in page)
            {
                if (_byId.ContainsKey(view.MessageId))
                {
                    continue;
                }

                var item = new MessageItemViewModel(view);
                _byId[view.MessageId] = item;
                Messages.Insert(index++, item);
            }

            _loadedCount += page.Count;
            RecalculateTimestampVisibility();
        }).ConfigureAwait(true);

        CanLoadMore = page.Count >= ProtocolConstants.LocalFirstPageSize;
    }

    /// <summary>重发（供视图内按钮调用；命令名为 <c>RetryMessageCommand</c>）。</summary>
    [RelayCommand(CanExecute = nameof(CanRetryMessage))]
    private async Task RetryMessageAsync(MessageItemViewModel? item)
    {
        if (item is null || !item.CanRetry)
        {
            return;
        }

        var result = await _chat.RetryAsync(item.MessageId).ConfigureAwait(true);
        if (!result.Success)
        {
            ShowNotice(I18n.T(result.I18nKey ?? ErrorCatalog.I18nKeyOf(result.ErrorCode)));
        }
    }

    private static bool CanRetryMessage(MessageItemViewModel? item) => item?.CanRetry == true;

    /// <summary>通知可定位到首屏之外的本地消息，分段 Bot 消息定位到第一段。</summary>
    public async Task LocateMessageAsync(string messageId)
    {
        while (!_byId.ContainsKey(messageId) && !_byId.ContainsKey(messageId + "_0") && CanLoadMore)
        {
            var count = Messages.Count;
            await LoadMoreAsync().ConfigureAwait(true);
            if (Messages.Count == count)
            {
                break;
            }
        }

        var target = _byId.ContainsKey(messageId) ? messageId : messageId + "_0";
        if (_byId.ContainsKey(target))
        {
            ScrollToMessageRequested?.Invoke(this, target);
        }
    }

    /// <summary>切换 Emoji 面板（**占位推起内容，不用浮层遮盖** —— FR-W-UI-5）。</summary>
    [RelayCommand]
    private void ToggleEmojiPanel() => IsEmojiPanelOpen = !IsEmojiPanelOpen;

    /// <summary>切换 Emoji 分类（分类平铺 —— FR-W-UI-5）。</summary>
    [RelayCommand]
    private void SelectEmojiCategory(string? category) => EmojiCategory = category ?? EmojiCategorySmileysKey;

    partial void OnEmojiCategoryChanged(string value) => FillEmojiPalette();

    /// <summary>
    /// 填充 Emoji 平铺集合（分类平铺 —— FR-W-UI-5）。
    /// ⚠️ emoji 字符本身是**符号数据**而非界面文案，故不属于 i18n 资源层管辖（NFR-W-10 只约束文案）；
    ///    分类**标签**仍全部走 <see cref="I18n"/>。
    /// </summary>
    private void FillEmojiPalette()
    {
        var glyphs = EmojiCategory switch
        {
            EmojiCategoryGesturesKey => GestureGlyphs,
            EmojiCategoryFacesKey => FaceGlyphs,
            EmojiCategoryFoodKey => FoodGlyphs,
            _ => SmileyGlyphs,
        };

        EmojiPalette.Clear();
        foreach (var glyph in glyphs)
        {
            EmojiPalette.Add(glyph);
        }
    }

    /// <summary>把选中的 emoji 插入输入框。</summary>
    [RelayCommand]
    private void InsertEmoji(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji))
        {
            return;
        }

        InputText += emoji;
    }

    /// <summary>把草稿缩略图集合的显隐同步到 <see cref="HasDrafts"/>。</summary>
    private void RaiseDraftDerived()
    {
        OnPropertyChanged(nameof(HasDrafts));
        OnPropertyChanged(nameof(RemoveDraftText));
    }

    /// <summary>发送当前输入（FR-W-CHAT-1/2/3/4；含附言合并的互动文本）。</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending || IsOfflineCache)
        {
            return;
        }

        var text = InputText;
        var attachments = _media.Drafts.ToList();
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return;
        }

        IsSending = true;
        try
        {
            // requestId 同时作为本地主键、WS 顶层 requestId 与幂等键（FR-W-CHAT-3）。
            var requestId = Guid.NewGuid().ToString("D");
            var result = await _chat
                .SendAsync(requestId, string.IsNullOrWhiteSpace(text) ? null : text, attachments)
                .ConfigureAwait(true);

            if (result.Success)
            {
                InputText = string.Empty;
                await _media.ClearDraftsAsync().ConfigureAwait(true);
                await RefreshDraftThumbnailsAsync().ConfigureAwait(true);
                return;
            }

            // 失败**不得**静默：给出错误码文案（或服务端下发的 i18n key）。
            ShowNotice(I18n.T(result.I18nKey ?? ErrorCatalog.I18nKeyOf(result.ErrorCode)));
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>重发一条失败的用户消息（**复用同一 requestId** —— FR-W-CHAT-11）。</summary>
    [RelayCommand]
    private async Task RetryAsync(MessageItemViewModel? item)
    {
        if (item is null || !item.CanRetry)
        {
            return;
        }

        var result = await _chat.RetryAsync(item.MessageId).ConfigureAwait(true);
        if (!result.Success)
        {
            ShowNotice(I18n.T(result.I18nKey ?? ErrorCatalog.I18nKeyOf(result.ErrorCode)));
        }
    }

    /// <summary>手动重连（FR-W-CONN-6 / FR-W-DSK-6 的 <c>Ctrl+Shift+R</c>）。</summary>
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        await _chat.ReconnectAsync().ConfigureAwait(true);
        ShowNotice(I18n.T("conn.reconnect.ok"));
    }

    /// <summary>删除一条本地消息（右键菜单）。</summary>
    [RelayCommand]
    private async Task DeleteMessageAsync(MessageItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_prompt.Confirm(I18n.T("chat.delete.confirm")))
        {
            return;
        }

        await _chat.DeleteLocalAsync(item.MessageId).ConfigureAwait(true);
    }

    /// <summary>
    /// 清空本地会话。⚠️ **必须**调 <see cref="IChatService.ClearLocalConversationAsync"/>，
    /// 与设置页共用同一实现（FR-W-CHAT-12）。
    /// </summary>
    [RelayCommand]
    private async Task ClearLocalAsync()
    {
        if (!_prompt.Confirm(I18n.T("chat.clear.confirm")))
        {
            return;
        }

        await _chat.ClearLocalConversationAsync().ConfigureAwait(true);

        await _dispatcher.InvokeAsync(() =>
        {
            _byId.Clear();
            Messages.Clear();
            _loadedCount = 0;
            CanLoadMore = false;
        }).ConfigureAwait(true);

        ShowNotice(I18n.T("chat.clear.done"));
    }

    /// <summary>添加图片（文件对话框由 View 层给出路径 —— FR-W-IMG-1）。</summary>
    public async Task AddImagesAsync(IReadOnlyList<string> sourcePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            return;
        }

        foreach (var path in sourcePaths)
        {
            var result = await _media.AddFromFileAsync(path, ct).ConfigureAwait(true);
            if (!result.Success)
            {
                // 失败必须可见（类型 / 体积 / 数量上限），不得静默丢弃（FR-W-IMG-2）。
                ShowNotice(I18n.T(result.ErrorI18nKey ?? "image.invalidType"));
                break;
            }
        }

        await RefreshDraftThumbnailsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 从剪贴板粘贴图片（<c>Ctrl+V</c> —— FR-W-IMG-8）。
    /// ⚠️ 剪贴板无图时**必须**给出 <c>image.paste.empty</c> 提示，**不得静默**。
    /// </summary>
    public async Task PasteImageAsync(CancellationToken ct = default)
    {
        var result = await _media.AddFromClipboardAsync(ct).ConfigureAwait(true);
        if (!result.Success)
        {
            ShowNotice(I18n.T(result.ErrorI18nKey ?? "image.paste.empty"));
            return;
        }

        await RefreshDraftThumbnailsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>移除一张草稿附件（缩略图上的单张移除按钮 —— FR-W-IMG-3）。</summary>
    [RelayCommand]
    private async Task RemoveDraftAsync(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var draft = _media.Drafts.FirstOrDefault(
            d => string.Equals(d.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));

        if (draft is null)
        {
            return;
        }

        await _media.RemoveDraftAsync(draft.AttachmentId).ConfigureAwait(true);
        await RefreshDraftThumbnailsAsync().ConfigureAwait(true);
    }

    /// <summary>打开互动浮层（右下角「+」；离线缓存态整体禁用 —— FR-W-PROG-4）。</summary>
    [RelayCommand]
    private void OpenInteractionMenu()
    {
        InteractionMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc 收回托盘（FR-W-DSK-6）。</summary>
    [RelayCommand]
    private void HideToTray() => HideToTrayRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>刷新未读通知数（历史入口红点）。</summary>
    public async Task RefreshUnreadAsync(CancellationToken ct = default)
    {
        var count = await _history.CountUnreadNotificationsAsync(ct).ConfigureAwait(true);
        await _dispatcher.InvokeAsync(() =>
        {
            UnreadNotificationCount = count;
            OnPropertyChanged(nameof(UnreadText));
        }).ConfigureAwait(true);
    }

    /// <summary>把当前 JS 层收集到的未读消息标记为已读（窗口回到前台时 —— FR-W-NOTI-5）。</summary>
    public async Task MarkVisibleIncomingReadAsync(CancellationToken ct = default)
    {
        if (_unreadIncoming.Count == 0)
        {
            return;
        }

        var ids = _unreadIncoming.ToList();
        _unreadIncoming.Clear();
        _ = await _history.MarkNotificationsReadAsync(ids, ct).ConfigureAwait(true);
        await RefreshUnreadAsync(ct).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _chat.ConnectionChanged -= OnConnectionChanged;
        _chat.TypingChanged -= OnTypingChanged;
        _chat.MessageChanged -= OnMessageChanged;
        _sync.ChatSynchronized -= OnChatSynchronized;
        _sync.SyncFailed -= OnSyncFailed;
        _chat.MessageRemoved -= OnMessageRemoved;
        _chat.ConversationCleared -= OnConversationCleared;
        _chat.NoticeRaised -= OnNoticeRaised;
        _chat.ScrollToMessageRequested -= OnScrollToMessageRequested;
        _gamification.ProgressChanged -= OnProgressChanged;

        GC.SuppressFinalize(this);
    }

    /* ------------------------------------------------------------------ */
    /* 内部：首屏 / 分页                                                    */
    /* ------------------------------------------------------------------ */

    private async Task LoadInitialPageAsync(CancellationToken ct)
    {
        var page = await _history
            .ListRecentMessagesAsync(ProtocolConstants.LocalFirstPageSize, 0, ct)
            .ConfigureAwait(true);

        await _dispatcher.InvokeAsync(() =>
        {
            _byId.Clear();
            Messages.Clear();

            foreach (var view in page)
            {
                AppendOrUpdate(view);
            }

            _loadedCount = page.Count;
            CanLoadMore = page.Count >= ProtocolConstants.LocalFirstPageSize;
            RecalculateTimestampVisibility();
        }).ConfigureAwait(true);
    }

    private async Task RestoreDraftsAsync(CancellationToken ct)
    {
        await _media.RestoreDraftsAsync(ct).ConfigureAwait(true);
        await RefreshDraftThumbnailsAsync(ct).ConfigureAwait(true);
    }

    private async Task RefreshDraftThumbnailsAsync(CancellationToken ct = default)
    {
        _ = ct;
        var paths = _media.Drafts.Select(static d => d.LocalPath).ToList();

        await _dispatcher.InvokeAsync(() =>
        {
            DraftThumbnails.Clear();
            foreach (var path in paths)
            {
                DraftThumbnails.Add(path);
            }

            RaiseDraftDerived();
        }).ConfigureAwait(true);
    }

    /// <summary>必须在 UI 线程调用（<see cref="IUiDispatcher"/> 内）。</summary>
    private void AppendOrUpdate(ChatMessageView view)
    {
        if (_byId.TryGetValue(view.MessageId, out var existing))
        {
            existing.Apply(view);
            return;
        }

        var item = new MessageItemViewModel(view);
        _byId[view.MessageId] = item;
        Messages.Add(item);
    }

    /// <summary>
    /// 重算时间显示：同日内、与前一条「非分隔线」消息间隔 ≤ 60 秒时合并（只保留较晚一条的时间）——
    /// FR-W-CHAT-13。跨日由 C-5 分隔线承担，因此跨日永远显示时间。
    /// </summary>
    private void RecalculateTimestampVisibility()
    {
        MessageItemViewModel? previous = null;
        foreach (var item in Messages)
        {
            if (item.IsHistorySeparator)
            {
                previous = null;
                continue;
            }

            if (previous is null)
            {
                item.ShowTimestamp = true;
            }
            else
            {
                var sameDay = previous.LocalTime.Date == item.LocalTime.Date;
                var deltaSeconds = Math.Abs((item.LocalTime - previous.LocalTime).TotalSeconds);
                item.ShowTimestamp = !(sameDay && deltaSeconds <= 60d);
            }

            previous = item;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 内部：服务事件                                                     */
    /* ------------------------------------------------------------------ */

    private void OnConnectionChanged(object? sender, ConnectionSnapshot snapshot)
        => _dispatcher.Invoke(() =>
        {
            ApplyConnection(snapshot);
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        });

    private void OnTypingChanged(object? sender, ChatTypingState state)
        => _dispatcher.Invoke(() => ApplyTyping(state));

    private void OnMessageChanged(object? sender, ChatMessageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _dispatcher.Invoke(() =>
        {
            var isNew = !_byId.ContainsKey(view.MessageId);
            AppendOrUpdate(view);

            if (isNew)
            {
                _loadedCount++;

                if (!string.Equals(view.Role, "user", StringComparison.Ordinal))
                {
                    _unreadIncoming.Add(view.MessageId);
                }
            }

            RecalculateTimestampVisibility();
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnMessageRemoved(object? sender, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        _dispatcher.Invoke(() =>
        {
            if (_byId.Remove(messageId, out var item))
            {
                _ = Messages.Remove(item);
                _loadedCount = Math.Max(0, _loadedCount - 1);
            }

            RecalculateTimestampVisibility();
        });
    }

    private void OnConversationCleared(object? sender, EventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
            _byId.Clear();
            _unreadIncoming.Clear();
            Messages.Clear();
            _loadedCount = 0;
            CanLoadMore = false;
            UnreadNotificationCount = 0;
            OnPropertyChanged(nameof(UnreadText));
            RecalculateTimestampVisibility();
        });
    }

    private void OnNoticeRaised(object? sender, string i18nKey)
    {
        if (string.IsNullOrWhiteSpace(i18nKey))
        {
            return;
        }

        _dispatcher.Invoke(() => ShowNotice(I18n.T(i18nKey)));
    }

    private void OnSyncFailed(object? sender, SyncFailure failure)
        => _dispatcher.Invoke(() => ShowNotice(I18n.T(failure.I18nKey)));

    private void OnChatSynchronized(object? sender, EventArgs e) => _ = MergeSynchronizedHistoryAsync();

    private async Task MergeSynchronizedHistoryAsync()
    {
        try
        {
            var page = await _history.ListRecentMessagesAsync(Math.Max(_loadedCount, ProtocolConstants.LocalFirstPageSize), 0).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                foreach (var view in page)
                {
                    AppendOrUpdate(view);
                }

                var sorted = Messages.OrderBy(static item => item.Timestamp).ThenBy(static item => item.MessageId, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < sorted.Length; i++)
                {
                    var oldIndex = Messages.IndexOf(sorted[i]);
                    if (oldIndex != i)
                    {
                        Messages.Move(oldIndex, i);
                    }
                }

                _loadedCount = Messages.Count;
                RecalculateTimestampVisibility();
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _dispatcher.Invoke(() => ShowNotice(I18n.T("error.unknown")));
        }
    }

    private void OnScrollToMessageRequested(object? sender, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        _dispatcher.Invoke(() => ScrollToMessageRequested?.Invoke(this, messageId));
    }

    private void OnProgressChanged(object? sender, ProgressSnapshot snapshot)
        => _dispatcher.Invoke(() => ApplyProgress(snapshot));

    /* ------------------------------------------------------------------ */
    /* 内部：状态映射                                                     */
    /* ------------------------------------------------------------------ */

    private void ApplyConnection(ConnectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ConnectionStatusText = I18n.T(snapshot.Status switch
        {
            ConnectionStatus.Connecting => "conn.status.connecting",
            ConnectionStatus.Connected => "conn.status.connected",
            ConnectionStatus.Reconnecting => "conn.status.reconnecting",
            ConnectionStatus.Refreshing => "conn.status.refreshing",
            ConnectionStatus.Degraded => "conn.status.degraded",
            ConnectionStatus.Disconnected => "conn.status.disconnected",
            _ => "conn.status.disconnected",
        }, snapshot.Attempt);

        IsDisconnected = snapshot.Status is ConnectionStatus.Disconnected
            or ConnectionStatus.Unauthenticated;
        IsDegraded = snapshot.Degraded || snapshot.Status == ConnectionStatus.Degraded;
    }

    private void ApplyTyping(ChatTypingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Typing)
        {
            IsTypingVisible = false;
            TypingText = string.Empty;
            return;
        }

        // FR-W-CHAT-14：Stage 决定文案；未知 / 空 stage 回退到通用「正在输入」（NFR-W-12）。
        TypingText = I18n.T(state.Stage switch
        {
            "typing" => "chat.typing",
            "vision" => "chat.typing.vision",
            "generating" => "chat.typing.generating",
            "interaction" => "chat.typing.interaction",
            "interaction_merge" => "chat.typing.interaction_merge",
            _ => "chat.typing",
        });

        IsTypingVisible = true;
    }

    private void ApplyProgress(ProgressSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        PointsBalance = snapshot.Balance;
        LevelDisplayName = LevelVisuals.ResolveDisplayName(snapshot.LevelCode, snapshot.LevelName);
        IsOfflineCache = snapshot.IsOfflineCache;
        OfflineNoticeText = snapshot.IsOfflineCache
            ? I18n.T("common.offlineData", FormatSyncTime(snapshot.SyncedAt))
            : string.Empty;

        OnPropertyChanged(nameof(CanInteract));
    }

    private void ShowNotice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        NoticeText = text;
        IsNoticeVisible = true;
    }

    /// <summary>文件选择器无法打开时由视图上报，确保失败不会静默或上升为顶层崩溃。</summary>
    public void ReportImagePickerFailure() => ShowNotice(I18n.T("image.fileDialog.failed"));

    /// <summary>轻量提示「已消费」（View 关闭提示条时调用）。</summary>
    public void ClearNotice()
    {
        NoticeText = string.Empty;
        IsNoticeVisible = false;
    }

    /// <summary>同步时刻 → 本地 `MM-DD HH:mm`（离线缓存提示用）。</summary>
    private static string FormatSyncTime(long unixMilliseconds)
        => MessageItemViewModel
            .FromUnixMilliseconds(unixMilliseconds)
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    /// <summary>表情类 emoji（分类平铺数据）。</summary>
    private static readonly string[] SmileyGlyphs =
    [
        "\U0001F600", "\U0001F603", "\U0001F604", "\U0001F601", "\U0001F606", "\U0001F605",
        "\U0001F602", "\U0001F642", "\U0001F643", "\U0001F609", "\U0001F60A", "\U0001F60D",
        "\U0001F618", "\U0001F617", "\U0001F61A", "\U0001F60B", "\U0001F61B", "\U0001F61C",
    ];

    /// <summary>手势类 emoji。</summary>
    private static readonly string[] GestureGlyphs =
    [
        "\U0001F44D", "\U0001F44E", "\U0001F44C", "\U0001F44F", "\U0001F64C", "\U0001F450",
        "\U0001F91D", "\U0001F64F", "\U0001F4AA", "\U0000270C", "\U0001F44B", "\U0001F91F",
        "\U0001F440", "\U0001F441",
    ];

    /// <summary>面孔 / 熊猫类 emoji。</summary>
    private static readonly string[] FaceGlyphs =
    [
        "\U0001F43C", "\U0001F43E", "\U0001F431", "\U0001F436", "\U0001F430", "\U0001F98A",
        "\U0001F43B", "\U0001F428", "\U0001F42F", "\U0001F981", "\U0001F42E", "\U0001F437",
        "\U0001F438", "\U0001F439",
    ];

    /// <summary>食物类 emoji。</summary>
    private static readonly string[] FoodGlyphs =
    [
        "\U0001F35C", "\U0001F35A", "\U0001F363", "\U0001F359", "\U0001F358", "\U0001F361",
        "\U0001F367", "\U0001F366", "\U0001F369", "\U0001F382", "\U00002615", "\U0001F375",
        "\U0001F37A", "\U0001F34E",
    ];
}
