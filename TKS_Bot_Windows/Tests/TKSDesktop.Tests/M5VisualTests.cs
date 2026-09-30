using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TKSDesktop.App;
using TKSDesktop.ViewModels;
using TKSDesktop.Views;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class M5VisualTests
{
    [Theory]
    [InlineData("emoji.smileys")]
    [InlineData("emoji.gestures")]
    [InlineData("emoji.hearts")]
    [InlineData("emoji.animals")]
    [InlineData("emoji.food")]
    [InlineData("emoji.objects")]
    public void EmojiCategories_HaveDistinctLocalizedLabelsAndGlyphs(string key)
    {
        var group = Assert.Single(EmojiGroup.All, entry => entry.Key == key);
        Assert.NotEqual(key, I18n.T(key));
        Assert.NotEmpty(group.Glyphs);
        Assert.Equal(group.Glyphs.Count, group.Glyphs.Distinct().Count());
    }

    // Called on the existing isolated WPF test's STA thread; no input automation.
    internal static void CheckResources(Application app, string root)
    {
        foreach (var shape in new[] { "Star", "Cloud", "Lightning", "Heart", "Circle", "Moon", "Smile", "Flower" })
            Assert.IsAssignableFrom<Geometry>(app.Resources[$"Tks.Doodle.{shape}"]);
        Assert.Equal(8, ((DrawingGroup)((DrawingBrush)app.Resources["Tks.Brush.DoodleTile"]).Drawing).Children.Count);

        var parent = new Border();
        var child = new Border { DataContext = new object(), Style = (Style)app.Resources["Tks.Style.GlassPanel"] };
        parent.Child = child;
        Assert.True(VisualPreferences.GetUseOpaqueFallback(child));
        VisualPreferences.SetUseOpaqueFallback(parent, false);
        Assert.False(VisualPreferences.GetUseOpaqueFallback(child));
        child.Measure(new Size(100, 100));
        Assert.NotNull(child.Effect);
        Assert.IsType<LinearGradientBrush>(child.Background);
        Assert.IsType<LinearGradientBrush>(child.BorderBrush);
        VisualPreferences.SetUseOpaqueFallback(parent, true);
        child.Measure(new Size(100, 100));
        Assert.Null(child.Effect);
        Assert.IsType<LinearGradientBrush>(child.Background);

        var manifestPath = Path.Combine(root, "asset-manifest.json");
        var entry = new AssetManifestEntry { LogicalName = "test", FileName = "badge.png", Status = "final", ExpectedSizes = [16, 32] };
        var manifest = new AssetManifest { Assets = [entry] };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Assert.Single(AssetValidation.Audit(manifestPath).Errors);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 128)));
        using (var output = File.Create(Path.Combine(root, entry.FileName))) encoder.Save(output);
        var valid = AssetValidation.Audit(manifestPath);
        Assert.Empty(valid.Errors);
        Assert.Equal(1, valid.FinalPassed);
        entry.ExpectedSizes = [64];
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        Assert.Single(AssetValidation.Audit(manifestPath).Errors);
        entry.FileName = "../escape.png";
        Assert.Throws<InvalidDataException>(() => AssetValidation.Load(root, entry));
        entry.Status = "placeholder";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        var placeholder = AssetValidation.Audit(manifestPath);
        Assert.Equal(1, placeholder.Placeholder);
        Assert.Equal(0, placeholder.FinalPassed);
        Assert.Empty(placeholder.Errors);

        var shipped = AssetValidation.Audit(Path.Combine(AppContext.BaseDirectory, AssetProvider.ManifestRelativePath));
        Assert.Empty(shipped.Errors);
        Assert.Equal(18, shipped.Total);
    }
}
