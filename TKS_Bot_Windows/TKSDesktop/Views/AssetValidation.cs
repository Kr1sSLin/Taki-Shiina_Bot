using System.Text.Json;
using System.Windows.Media.Imaging;

namespace TKSDesktop.Views;

public sealed record AssetAudit(int Total, int Placeholder, int FinalPassed, IReadOnlyList<string> Errors);

/// <summary>PNG uses the largest declared size; ICO must contain every declared frame.</summary>
public static class AssetValidation
{
    public static AssetAudit Audit(string manifestPath)
    {
        var errors = new List<string>();
        var total = 0;
        var placeholder = 0;
        var passed = 0;
        try
        {
            var manifest = JsonSerializer.Deserialize<AssetManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("Empty manifest");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in manifest.Assets)
            {
                total++;
                if (string.IsNullOrWhiteSpace(entry.LogicalName) || !names.Add(entry.LogicalName))
                    errors.Add($"Duplicate or empty logical name: {entry.LogicalName}");
                if (entry.Status == "placeholder") { placeholder++; continue; }
                if (entry.Status != "final") { errors.Add($"Invalid status: {entry.LogicalName}"); continue; }
                try { Load(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, entry); passed++; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { errors.Add($"{entry.LogicalName}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { errors.Add(ex.Message); }
        return new AssetAudit(total, placeholder, passed, errors);
    }

    public static BitmapFrame Load(string directory, AssetManifestEntry entry)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, entry.FileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(entry.FileName))
            throw new InvalidDataException("Asset path escapes manifest directory");
        if (entry.ExpectedSizes.Length == 0 || entry.ExpectedSizes.Any(size => size <= 0))
            throw new InvalidDataException("Expected sizes must be positive");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
        var largest = decoder.Frames.MaxBy(frame => (long)frame.PixelWidth * frame.PixelHeight)
            ?? throw new InvalidDataException("Image has no frames");
        var sizes = Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? entry.ExpectedSizes : [entry.ExpectedSizes.Max()];
        if (sizes.Any(size => !decoder.Frames.Any(frame => frame.PixelWidth == size && frame.PixelHeight == size)))
            throw new InvalidDataException("Image dimensions do not match expectedSizes");
        largest.Freeze();
        return largest;
    }
}
