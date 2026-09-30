using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TKSDesktop.Contracts;

namespace TKSDesktop.Views;

/// <summary>
/// 程序化绘制的等级徽章（PRD FR-W-UI-12 兜底 / FR-W-LV-1 / FR-W-LV-4）。
///
/// <list type="bullet">
///   <item>配色 / emoji 一律来自 C-1 <see cref="LevelVisuals"/>（**不得另存一份色值**）；</item>
///   <item>空 / 未知 <c>LevelCode</c> 回退中性兜底视觉（EDGE-W-24 / FR-W-LV-3）；</item>
///   <item>美术资源到位前不依赖任何美术文件 —— 全部自绘（FR-W-UI-12）。</item>
/// </list>
/// </summary>
public sealed class PandaBadge : Control
{
    /// <summary>等级码（`PANDA_LV1..7` / `NONE`）。</summary>
    public static readonly DependencyProperty LevelCodeProperty = DependencyProperty.Register(
        nameof(LevelCode),
        typeof(string),
        typeof(PandaBadge),
        new FrameworkPropertyMetadata(
            LevelVisuals.DefaultLevelCode,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnLevelCodeChanged));

    /// <summary>徽章直径。</summary>
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter),
        typeof(double),
        typeof(PandaBadge),
        new FrameworkPropertyMetadata(72d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>是否允许动态（LV7 七彩 / 呼吸光环）。系统「减少动画」时必须为 <c>false</c>。</summary>
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated),
        typeof(bool),
        typeof(PandaBadge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimationFlagChanged));

    private static readonly DependencyPropertyKey GlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Glyph),
        typeof(string),
        typeof(PandaBadge),
        new PropertyMetadata(LevelVisuals.Default.Emoji));

    /// <summary>当前显示的 emoji（只读，供模板 / 调试查看）。</summary>
    public static readonly DependencyProperty GlyphProperty = GlyphPropertyKey.DependencyProperty;

    /// <summary>当前徽章采用的高亮/强调画刷（只读）。</summary>
    private static readonly DependencyPropertyKey AccentBrushPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(PandaBadge),
        new PropertyMetadata(new SolidColorBrush(Colors.Transparent)));

    /// <summary>强调色画刷（只读）。</summary>
    public static readonly DependencyProperty AccentBrushProperty = AccentBrushPropertyKey.DependencyProperty;

    private readonly RotateTransform _rainbowRotation = new(0d);
    public static readonly DependencyProperty ArtworkProperty = DependencyProperty.Register(
        nameof(Artwork), typeof(ImageSource), typeof(PandaBadge));
    public ImageSource? Artwork => (ImageSource?)GetValue(ArtworkProperty);

    /// <summary>构造：默认使用自带模板（无资源依赖）。</summary>
    public PandaBadge()
    {
        DefaultStyleKey = typeof(PandaBadge);
        Loaded += (_, _) => ApplyVisual();
        Unloaded += (_, _) => _rainbowRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        IsVisibleChanged += (_, _) => ApplyVisual();
        ApplyVisual();
    }

    /// <summary>等级码。</summary>
    public string LevelCode
    {
        get => (string)GetValue(LevelCodeProperty);
        set => SetValue(LevelCodeProperty, value);
    }

    /// <summary>直径。</summary>
    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    /// <summary>是否允许动态效果（LV7 七彩）。</summary>
    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    /// <summary>当前 emoji。</summary>
    public string Glyph => (string)GetValue(GlyphProperty);

    /// <summary>强调色画刷。</summary>
    public Brush AccentBrush => (Brush)GetValue(AccentBrushProperty);

    /// <summary>解析出的视觉（供调用方 / 模板使用）。</summary>
    public LevelVisual Visual => LevelVisuals.Resolve(LevelCode);

    private static void OnLevelCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PandaBadge)d).ApplyVisual();

    private static void OnAnimationFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PandaBadge)d).ApplyVisual();

    /// <summary>把 <see cref="LevelVisuals"/> 的取值推入依赖属性并（可选）启动七彩动画。</summary>
    private void ApplyVisual()
    {
        var visual = LevelVisuals.Resolve(LevelCode);

        SetValue(GlyphPropertyKey, visual.Emoji);
        SetValue(ArtworkProperty, AssetProvider.TryLoadImage($"level.{visual.Code}.badge", out var image) ? image : null);
        SetValue(AccentBrushPropertyKey, Views.Converters.LevelAccentBrushConverter.BrushFor(visual.Accent));

        var animated = IsAnimated && visual.Animated && IsVisible && SystemParameters.ClientAreaAnimation;

        if (!animated)
        {
            _rainbowRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _rainbowRotation.Angle = 0d;
            RenderTransform = null;
            return;
        }

        // Animate the ring's paint, never rotate the face or label.
        _rainbowRotation.CenterX = 0.5;
        _rainbowRotation.CenterY = 0.5;
        var rainbow = new LinearGradientBrush
        {
            RelativeTransform = _rainbowRotation,
            GradientStops = new GradientStopCollection
            {
                new(Colors.Coral, 0), new(Colors.Gold, 0.2), new(Colors.LimeGreen, 0.4),
                new(Colors.DeepSkyBlue, 0.6), new(Colors.MediumPurple, 0.8), new(Colors.Coral, 1),
            },
        };
        SetValue(AccentBrushPropertyKey, rainbow);

        // ≈2.4s 一圈，避免过快造成视觉噪声；系统减少动画时根本不会走到这里。
        var animation = new DoubleAnimation(0d, 360d, TimeSpan.FromSeconds(2.4))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        _rainbowRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        var side = Math.Max(16d, Diameter);
        return new Size(side, side);
    }
}
