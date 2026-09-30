using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TKSDesktop.App;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;

namespace TKSDesktop.Views;

/// <summary>
/// 聊天视图（FR-W-CHAT-1..18 / FR-W-IMG-1/3/7/8/9 / FR-W-UI-5 / FR-W-DSK-6）。
///
/// 代码隐藏只处理**纯视图职责**（键盘手势、滚动、文件对话框交互与错误提示）；
/// 业务逻辑全部在 <see cref="ViewModels.ChatViewModel"/> 内，视图不直接触碰服务（除滚动订阅所需的最小读取）。
/// </summary>
public partial class ChatView : UserControl
{
    private ViewModels.ChatViewModel? _viewModel;

    /// <summary>构造。</summary>
    public ChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>请求打开图片大图查看器（滚轮缩放 + 另存为 —— FR-W-IMG-7/9）。</summary>
    public event EventHandler<string>? ImageOpenRequested;

    /// <summary>Esc 收回托盘（FR-W-DSK-6）。</summary>
    public event EventHandler? HideToTrayRequested;

    public void FocusInput() => InputBox.Focus();

    private void ComposerCapsule_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // WPF normalizes oversized radii independently on each axis. A value such
        // as 999 makes a wide border an ellipse, not a pill with straight edges.
        if (sender is Border capsule)
        {
            capsule.CornerRadius = new CornerRadius(Math.Min(e.NewSize.Width, e.NewSize.Height) / 2);
        }
    }

    public void OpenSearch()
    {
        if (_viewModel is not { } chat)
        {
            return;
        }

        chat.IsSearchOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } chat)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            chat.SearchMessagesCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            chat.CloseSearchCommand.Execute(null);
        }
    }

    private void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } chat && sender is Button { DataContext: ViewModels.ChatSearchResult result })
        {
            chat.JumpToSearchResultCommand.Execute(result);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
        InputBox.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        Attach();
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not ViewModels.ChatViewModel chat)
        {
            return;
        }

        _viewModel = chat;
        chat.ScrollToEndRequested += OnScrollToEnd;
        chat.ScrollToMessageRequested += OnScrollToMessage;
        chat.HideToTrayRequested += OnHideToTray;
    }

    private void Detach()
    {
        var chat = _viewModel;
        if (chat is null)
        {
            return;
        }

        chat.ScrollToEndRequested -= OnScrollToEnd;
        chat.ScrollToMessageRequested -= OnScrollToMessage;
        chat.HideToTrayRequested -= OnHideToTray;
        _viewModel = null;
    }

    private ScrollViewer? MessageScrollViewer => MessageItems.Template?.FindName("MessageScrollViewer", MessageItems) as ScrollViewer;

    private void OnScrollToEnd(object? sender, EventArgs e)
    {
        MessageItems.ApplyTemplate();
        MessageScrollViewer?.ScrollToEnd();
    }

    private void OnScrollToMessage(object? sender, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var item = MessageItems.Items
            .Cast<object>()
            .OfType<ViewModels.MessageItemViewModel>()
            .FirstOrDefault(candidate => string.Equals(candidate.MessageId, messageId, StringComparison.Ordinal));

        if (item is null)
        {
            return;
        }

        MessageItems.UpdateLayout();
        // A virtualized target may not have a container yet. Realize it by index first.
        var presenter = FindVirtualizingPanel(MessageItems);
        presenter?.BringIndexIntoViewPublic(MessageItems.Items.IndexOf(item));
        MessageItems.UpdateLayout();
        var container = MessageItems.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        container?.BringIntoView();
    }

    private static VirtualizingPanel? FindVirtualizingPanel(DependencyObject parent)
    {
        if (parent is VirtualizingPanel panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindVirtualizingPanel(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        }
        return null;
    }

    private void OnHideToTray(object? sender, EventArgs e)
        => HideToTrayRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 输入框键盘行为（FR-W-CHAT-1 / FR-W-SET-8）：
    /// <list type="bullet">
    ///   <item>发送键 = Enter：Enter 发送、Shift+Enter 换行；</item>
    ///   <item>发送键 = Ctrl+Enter：Ctrl+Enter 发送、Enter 换行；</item>
    ///   <item>Esc：收回托盘（FR-W-DSK-6）。</item>
    /// </list>
    /// </summary>
    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } chat)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            chat.HideToTrayCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Ctrl+Enter 发送模式：Enter 只换行（Shift 无关）。
        if (chat.IsSendKeyCtrlEnter)
        {
            if (!control)
            {
                return;
            }

            e.Handled = true;
            chat.SendCommand.Execute(null);
            return;
        }

        // Enter 发送模式：Shift+Enter 保持换行。
        if (shift)
        {
            return;
        }

        e.Handled = true;
        chat.SendCommand.Execute(null);
    }

    /// <summary>重发（复用同一 requestId —— FR-W-CHAT-11）。</summary>
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ViewModels.MessageItemViewModel item }
            && _viewModel is { } chat)
        {
            chat.RetryMessageCommand.Execute(item);
        }
    }

    /// <summary>移除一张草稿附件（FR-W-IMG-3）。</summary>
    private void RemoveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string path } && _viewModel is { } chat)
        {
            chat.RemoveDraftCommand.Execute(path);
        }
    }

    /// <summary>创建符合 WPF 约定的“说明|通配符”筛选器，避免 OpenFileDialog 抛出参数异常。</summary>
    internal static string BuildImageDialogFilter()
        => $"{I18n.T("image.fileDialog.filter")}|*.jpg;*.jpeg;*.png";

    /// <summary>由视图拥有文件选择器；成功后只把已选路径交给 ViewModel 处理。</summary>
    private async void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } chat)
        {
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = I18n.T("image.fileDialog.title"),
                Filter = BuildImageDialogFilter(),
                Multiselect = true,
                CheckFileExists = true,
                RestoreDirectory = true,
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true && dialog.FileNames.Length > 0)
            {
                await chat.AddImagesAsync(dialog.FileNames).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            chat.ReportImagePickerFailure();
        }
    }

    /// <summary>插入 emoji 到输入框（FR-W-UI-5）。</summary>
    private void InsertEmoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string emoji } && _viewModel is { } chat)
        {
            var caret = InputBox.SelectionStart;
            InputBox.SelectedText = emoji;
            InputBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            InputBox.Focus();
            InputBox.CaretIndex = caret + emoji.Length;
        }
    }

    /// <summary>点击附件缩略图 → 打开大图（滚轮缩放 + 另存为 —— FR-W-IMG-7/9）。</summary>
    private void Thumbnail_OpenRequested(object? sender, string path)
        => ImageOpenRequested?.Invoke(this, path);

    /// <summary>从剪贴板粘贴图片（<c>Ctrl+V</c>）；空剪贴板**必须有提示**，不得静默（FR-W-IMG-8）。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_viewModel is not { } chat)
        {
            return;
        }

        if (InputBox.IsKeyboardFocusWithin && e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = chat.PasteImageAsync();
        }
    }
}

/// <summary>
/// 本地绝对路径 → <see cref="ImageSource"/>（FR-W-SEC-6 越界校验 / FR-W-IMG-9 缺失处理）。
/// ⚠️ 任何失败（越界 / 不存在 / 解码失败）都返回 <c>null</c>，**不抛错**（NFR-W-12）。
/// </summary>
public sealed class LocalImageConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Load(value as string);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>加载本地图片；失败返回 <c>null</c>。</summary>
    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        // ⚠️ FR-W-SEC-6：只在附件私有目录内才允许加载，越界直接拒绝（EDGE-W-29）。
        var attachmentsRoot = AppContext.GetData("Tks.AttachmentsDir") as string;
        if (!string.IsNullOrWhiteSpace(attachmentsRoot))
        {
            var root = Path.GetFullPath(attachmentsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path);

            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        try
        {
            var frame = BitmapFrame.Create(
                new Uri(Path.GetFullPath(path), UriKind.Absolute),
                BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);

            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 图片损坏 / 格式不支持 → 不显示而不是崩溃（NFR-W-12）。
            return null;
        }
    }
}

/// <summary>
/// 附件缩略图（FR-W-IMG-7）：点击后请求打开大图查看器。
/// 图片加载失败时显示占位（由 <see cref="ThumbnailKind"/> 决定），**不抛错**。
/// </summary>
public sealed class ImageThumbnail : Button
{
    /// <summary>本地绝对路径。</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path),
        typeof(string),
        typeof(ImageThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPathChanged));

    private readonly Image _image = new()
    {
        Stretch = Stretch.UniformToFill,
    };

    /// <summary>构造。</summary>
    public ImageThumbnail()
    {
        Padding = new Thickness(0);
        MinHeight = 0;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(1);
        BorderBrush = (Brush)FindResource("Tks.Brush.Border");
        Content = _image;
        Cursor = Cursors.Hand;
        Click += OnClick;
    }

    /// <summary>请求打开大图（参数为本地绝对路径）。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>本地绝对路径。</summary>
    public string? Path
    {
        get => (string?)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImageThumbnail thumbnail)
        {
            thumbnail.RefreshSource();
        }
    }

    private void RefreshSource() => _image.Source = LocalImageConverter.Load(Path);

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Path))
        {
            OpenRequested?.Invoke(this, Path);
        }
    }
}
