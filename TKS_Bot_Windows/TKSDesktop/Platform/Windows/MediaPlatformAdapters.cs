using System.Windows.Media.Imaging;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Platform.Windows;

/// <summary>附件文件系统实现；所有探测/删除失败均按端口约定收敛。</summary>
public sealed class WindowsFileStorage : IFileStoragePort
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception) { return 0; }
    }

    public byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception) { return null; }
    }

    public void WriteAllBytes(string path, byte[] content) => File.WriteAllBytes(path, content);

    public void CopyFile(string sourcePath, string destinationPath) => File.Copy(sourcePath, destinationPath, overwrite: true);

    public bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public long GetAvailableFreeSpaceBytes(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrWhiteSpace(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return long.MaxValue;
        }
    }

    public IReadOnlyList<string> EnumerateFileNames(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory) : []; }
        catch (Exception) { return []; }
    }
}

/// <summary>WPF 图片解码器适配；Core 层不直接引用 WPF。</summary>
public sealed class WpfImageProbe : IImageProbe
{
    public (int Width, int Height)? TryReadDimensions(string path, string mimeType)
    {
        _ = mimeType;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            return frame is null ? null : (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? TryDetectMimeType(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Read(header) < 3)
            {
                return null;
            }

            if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            {
                return "image/jpeg";
            }

            ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            return header.SequenceEqual(png) ? "image/png" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
