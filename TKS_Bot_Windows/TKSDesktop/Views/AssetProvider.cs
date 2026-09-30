using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TKSDesktop.Views;

/// <summary><c>asset-manifest.json</c> 中的一项（PRD FR-W-UI-12 / V-W-S12）。</summary>
public sealed class AssetManifestEntry
{
    [JsonPropertyName("expectedSizes")]
    public int[] ExpectedSizes { get; set; } = [];

    /// <summary>逻辑名（**代码只引用逻辑名，不得写死文件名**）。</summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = string.Empty;

    /// <summary>实际文件名。</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>`icon` / `badge` / `itemIcon` / `image`。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>`final` / `placeholder`（**必须如实计数** —— V-W-S12）。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "placeholder";
}

/// <summary><c>asset-manifest.json</c> 根对象。</summary>
public sealed class AssetManifest
{
    /// <summary>资源项。</summary>
    [JsonPropertyName("assets")]
    public List<AssetManifestEntry> Assets { get; set; } = [];
}

/// <summary>
/// 美术资源解析（PRD FR-W-UI-12 / V-W-S12）。
///
/// <list type="bullet">
///   <item>读 <c>Resources/Assets/asset-manifest.json</c>，把**逻辑名**解析为实际资源；</item>
///   <item>⚠️ 资源缺失 / 清单缺失 / 解码失败 → **回退程序化绘制占位**，**不得抛错**；</item>
///   <item>⚠️ 当前清单 18 项**全部为 `placeholder`**（OQ-W-8 答复「等后续」），
///         因此本类在占位期一律返回程序化占位图，且 <see cref="IsPlaceholder"/> 如实为 <c>true</c>。</item>
/// </list>
/// </summary>
public static class AssetProvider
{
    /// <summary>清单相对路径。</summary>
    public const string ManifestRelativePath = "Resources/Assets/asset-manifest.json";

    /// <summary>资源目录相对路径（pack URI 与磁盘探测共用）。</summary>
    public const string AssetsRelativePath = "Resources/Assets";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource> PlaceholderCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FileNameByLogicalName = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.Ordinal);

    private static bool _manifestLoaded;
    private static string? _manifestDirectory;
    private static readonly Dictionary<string, AssetManifestEntry> Entries = new(StringComparer.Ordinal);

    /// <summary>全部已登记的逻辑名（供机械校验）。</summary>
    public static IReadOnlyCollection<string> LogicalNames
    {
        get
        {
            EnsureManifest();
            lock (Gate)
            {
                return FileNameByLogicalName.Keys.ToArray();
            }
        }
    }

    /// <summary>
    /// 取图片。**永不抛错**：资源缺失 / 解码失败一律返回程序化占位图（FR-W-UI-12）。
    /// </summary>
    public static ImageSource GetImage(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return PlaceholderFor("unknown");
        }

        if (!TryLoadImage(logicalName, out var image))
        {
            return PlaceholderFor(logicalName);
        }

        return image;
    }

    /// <summary>
    /// 尝试加载真实资源。当前清单全为 `placeholder` 时直接返回 <c>false</c>
    /// （避免对不存在的 SVG 做无意义的解码尝试）。
    /// </summary>
    public static bool TryLoadImage(string logicalName, out ImageSource image)
    {
        image = null!;
        EnsureManifest();

        if (IsPlaceholder(logicalName))
        {
            return false;
        }

        var fileName = ResolveFileName(logicalName);
        if (fileName is null)
        {
            return false;
        }

        try
        {
            if (_manifestDirectory is null || !Entries.TryGetValue(logicalName, out var entry)) return false;
            AssetValidation.Load(_manifestDirectory, entry);
            // Keep a URI-backed source for the native tray image converter.
            var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(Path.Combine(_manifestDirectory, entry.FileName))),
                BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            frame.Freeze();
            image = frame;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 资源未随包 / 格式不被 WPF 解码（SVG）/ 文件损坏 → 一律回退占位（FR-W-UI-12）。
            return false;
        }
    }

    /// <summary>逻辑名 → 清单中的文件名；未登记时为 <c>null</c>。</summary>
    public static string? ResolveFileName(string logicalName)
    {
        EnsureManifest();

        lock (Gate)
        {
            return FileNameByLogicalName.TryGetValue(logicalName, out var fileName) ? fileName : null;
        }
    }

    /// <summary>
    /// 该逻辑名在清单中是否仍为 `placeholder`（**如实反映，不得谎报** —— V-W-S12）。
    /// 未登记的逻辑名视为占位。
    /// </summary>
    public static bool IsPlaceholder(string logicalName)
    {
        EnsureManifest();

        lock (Gate)
        {
            return !FileNameByLogicalName.ContainsKey(logicalName) || PlaceholderNames.Contains(logicalName);
        }
    }

    /// <summary>当前清单中 `placeholder` 项数与总项数（供发布检查如实计数）。</summary>
    public static (int Total, int Placeholder) CountAssets()
    {
        EnsureManifest();

        lock (Gate)
        {
            return (FileNameByLogicalName.Count, PlaceholderNames.Count);
        }
    }

    /// <summary>
    /// 程序化占位图（FR-W-UI-12 的最终兜底）：按逻辑名派生稳定的中性配色，
    /// 画一个圆角方块 + 十字标记，**不依赖任何美术资源**。
    /// </summary>
    public static ImageSource PlaceholderFor(string logicalName)
    {
        var key = string.IsNullOrWhiteSpace(logicalName) ? "unknown" : logicalName;

        lock (Gate)
        {
            if (PlaceholderCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var drawing = new DrawingGroup();
        var (from, to) = PaletteFor(key);

        var background = new GeometryDrawing
        {
            Geometry = new RectangleGeometry(new System.Windows.Rect(0, 0, 64, 64), 14, 14),
            Brush = new LinearGradientBrush(from, to, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1)),
        };

        var mark = new GeometryDrawing
        {
            Geometry = new GeometryGroup
            {
                Children =
                {
                    new RectangleGeometry(new System.Windows.Rect(28, 16, 8, 32), 2, 2),
                    new RectangleGeometry(new System.Windows.Rect(16, 28, 32, 8), 2, 2),
                },
            },
            Brush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        };

        drawing.Children.Add(background);
        drawing.Children.Add(mark);

        var image = new DrawingImage(drawing);
        image.Freeze();

        lock (Gate)
        {
            PlaceholderCache[key] = image;
        }

        return image;
    }

    /// <summary>读取清单（缺失 / 损坏时**不抛错**，视为「全部为占位」）。</summary>
    private static void EnsureManifest()
    {
        lock (Gate)
        {
            if (_manifestLoaded)
            {
                return;
            }

            _manifestLoaded = true;

            foreach (var candidate in ManifestCandidates())
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(candidate);
                    var manifest = JsonSerializer.Deserialize<AssetManifest>(json);
                    if (manifest is null)
                    {
                        continue;
                    }

                    foreach (var entry in manifest.Assets)
                    {
                        if (string.IsNullOrWhiteSpace(entry.LogicalName))
                        {
                            continue;
                        }

                        FileNameByLogicalName[entry.LogicalName] = entry.FileName;
                        Entries[entry.LogicalName] = entry;

                        if (!string.Equals(entry.Status, "final", StringComparison.Ordinal))
                        {
                            PlaceholderNames.Add(entry.LogicalName);
                        }
                    }

                    _manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(candidate));
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // 清单不可读 / 损坏：保持「无登记」状态 → 全部走程序化占位（FR-W-UI-12）。
                }
            }
        }
    }

    private static IEnumerable<string> ManifestCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ManifestRelativePath);
        yield return Path.Combine(Environment.CurrentDirectory, ManifestRelativePath);

        // 开发期（`dotnet run` 从仓库目录启动）时的兜底探测。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 4 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, ManifestRelativePath);
            dir = dir.Parent;
        }
    }

    /// <summary>由逻辑名派生稳定的中性配色（同一逻辑名恒得同一配色）。</summary>
    private static (Color From, Color To) PaletteFor(string logicalName)
    {
        var hash = 17;
        foreach (var ch in logicalName)
        {
            hash = unchecked((hash * 31) + ch);
        }

        var hue = Math.Abs(hash % 360);

        return (
            FromHsl(hue, 0.34, 0.78),
            FromHsl((hue + 28) % 360, 0.36, 0.60));
    }

    /// <summary>HSL → RGB（不引入额外依赖）。</summary>
    private static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        var c = (1d - Math.Abs((2d * lightness) - 1d)) * saturation;
        var h = hueDegrees / 60d;
        var x = c * (1d - Math.Abs((h % 2d) - 1d));

        var (r1, g1, b1) = h switch
        {
            < 1d => (c, x, 0d),
            < 2d => (x, c, 0d),
            < 3d => (0d, c, x),
            < 4d => (0d, x, c),
            < 5d => (x, 0d, c),
            _ => (c, 0d, x),
        };

        var m = lightness - (c / 2d);

        return Color.FromRgb(
            (byte)Math.Clamp((r1 + m) * 255d, 0d, 255d),
            (byte)Math.Clamp((g1 + m) * 255d, 0d, 255d),
            (byte)Math.Clamp((b1 + m) * 255d, 0d, 255d));
    }
}

/// <summary>
/// 逻辑名 → 程序化占位图（FR-W-UI-12 的最终兜底）。
/// ⚠️ 输入可以是**逻辑名**（走清单解析与占位回退）或任意字符串（同逻辑名恒得同一配色）。
/// </summary>
public sealed class PlaceholderImageConverter : System.Windows.Data.IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => AssetProvider.GetImage(value as string ?? string.Empty);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
