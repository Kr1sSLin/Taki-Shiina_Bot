using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TKSDesktop.App;

namespace TKSDesktop.Views;

/// <summary>
/// Large-image viewer (FR-W-IMG-9).
///
/// <para>Opens a bitmap from an app-private attachment path with wheel zoom,
/// zoom reset and "save as". The path is expected to have been validated against
/// the attachment directory by the caller (<c>IPaths.IsInsideAttachments</c>,
/// FR-W-SEC-6 / EDGE-W-29) — this window never resolves arbitrary paths by itself.</para>
///
/// <para>Load failures degrade to a plain message instead of crashing, because a
/// user may delete the underlying file between the message list rendering and the
/// click (NFR-W-12).</para>
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double WheelStep = 1.15;

    private readonly string _path;
    private double _scale = 1.0;
    private bool _loadFailed;

    /// <summary>构造。</summary>
    /// <param name="path">已校验位于附件私有目录内的图片绝对路径。</param>
    public ImageViewerWindow(string path)
    {
        _path = path ?? string.Empty;

        InitializeComponent();
        DataContext = this;

        TryLoadImage();
    }

    /// <summary>窗口标题。</summary>
    public string ViewerTitle => I18n.T("image.save");

    /// <summary>放大按钮文案。</summary>
    public string ZoomInText => I18n.T("image.zoomIn");

    /// <summary>缩小按钮文案。</summary>
    public string ZoomOutText => I18n.T("image.zoomOut");

    /// <summary>重置缩放按钮文案。</summary>
    public string ZoomResetText => I18n.T("image.resetZoom");

    /// <summary>另存为按钮文案。</summary>
    public string SaveAsText => I18n.T("image.save");

    /// <summary>关闭按钮文案。</summary>
    public string CloseText => I18n.T("common.close");

    private void TryLoadImage()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            _loadFailed = true;
            PreviewImage.Source = null;
            Title = I18n.T("image.notFound");
            return;
        }

        try
        {
            // OnLoad + 不锁定文件句柄：用户随时可以移动/删除原文件而不影响查看。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(_path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            PreviewImage.Source = image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            _loadFailed = true;
            PreviewImage.Source = null;
            Title = I18n.T("image.pathRejected");
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyScale(_scale * WheelStep);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyScale(_scale / WheelStep);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ApplyScale(1.0);

    private void ImageScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        ApplyScale(e.Delta > 0 ? _scale * WheelStep : _scale / WheelStep);
        e.Handled = true;
    }

    private void ApplyScale(double scale)
    {
        if (_loadFailed)
        {
            return;
        }

        _scale = Math.Clamp(scale, MinScale, MaxScale);

        // 用 ScaleTransform 而非改 Width/Height：保持矢量/位图清晰度与布局稳定。
        PreviewImage.LayoutTransform = new System.Windows.Media.ScaleTransform(_scale, _scale);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource source)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(_path),
            DefaultExt = Path.GetExtension(_path),
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // 按目标扩展名选择编码器；未知扩展名回退 PNG（无损，不会二次劣化）。
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                _ => new PngBitmapEncoder(),
            };

            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 保存失败不得让窗口崩溃；如实提示。
            MessageBox.Show(
                this,
                I18n.T("image.diskFull"),
                I18n.T("common.confirm"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
