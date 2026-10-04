using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TKSDesktop.Views;

/// <summary>Use bundled color artwork instead of WPF's monochrome font glyphs.</summary>
public sealed class EmojiImageConverter : IValueConverter
{
    private readonly Dictionary<string, BitmapImage> _images = new(StringComparer.Ordinal);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string glyph) return Binding.DoNothing;
        if (_images.TryGetValue(glyph, out var cached)) return cached;
        var code = string.Join("-", glyph.EnumerateRunes()
            .Where(rune => rune.Value != 0xfe0f)
            .Select(rune => rune.Value.ToString("x", CultureInfo.InvariantCulture)));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Resources", "Assets", "Emoji", code + ".png"));
        image.EndInit();
        image.Freeze();
        _images.Add(glyph, image);
        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
