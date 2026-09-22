using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using TKSDesktop.Core.Platform;

namespace TKSDesktop.Platform.Windows;

/// <summary>
/// 剪贴板贴图（PRD FR-W-IMG-8 / FR-W-DSK-5、US-W-3）。
///
/// 契约：
/// <list type="bullet">
///   <item>**PNG 优先**：先试 <c>Clipboard.GetDataObject()</c> 的 <c>PNG</c> 格式
///         （Windows 10 截图工具与浏览器提供，且**保留 alpha**）；</item>
///   <item>否则回退 <c>CF_DIB</c> / <c>Bitmap</c> → <c>BitmapSource</c> → <see cref="PngBitmapEncoder"/> 编码为 PNG；</item>
///   <item>无图片返回 <c>null</c>；<see cref="HasImage"/> 与 <see cref="TryReadPng"/> **都不得抛错**；</item>
///   <item>剪贴板被其他进程锁定（<see cref="COMException"/>）→ 返回 <c>null</c> / <c>false</c>。</item>
/// </list>
///
/// ⚠️ <c>System.Windows.Clipboard</c> 要求 **STA** 线程（进程入口已有 <c>[STAThread]</c>）。
/// </summary>
public sealed class ClipboardImageReader : IClipboardImage
{
    /// <summary>调用方可用于提示「无图片」的 i18n 键（FR-W-IMG-8：不得静默无反应）。</summary>
    public const string EmptyClipboardI18nKey = "chat.clipboard.empty";

    /// <inheritdoc />
    public bool HasImage()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
            {
                return false;
            }

            return data.GetDataPresent(NativeMethods.ClipboardFormatPng)
                || data.GetDataPresent(NativeMethods.ClipboardFormatDib)
                || data.GetDataPresent(DataFormats.Bitmap);
        }
        catch (COMException)
        {
            // 剪贴板被其他进程锁定（含剪贴板查看器 / 远程桌面重定向），按「无图片」处理。
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (Exception)
        {
            // 剪贴板不可用（无桌面会话等）不得让快捷键处理路径崩溃。
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>成功返回 PNG 字节；无图片/被锁定/解码失败一律返回 <c>null</c>。</remarks>
    public byte[]? TryReadPng()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null)
            {
                return null;
            }

            // ① PNG 格式优先（含 alpha，避免 CF_DIB 丢透明度后重编码）。
            var png = TryReadPngFormat(data);
            if (png is not null)
            {
                return png;
            }

            // ② CF_DIB（DeviceIndependentBitmap）→ BitmapSource → PNG。
            var dib = TryReadDibFormat(data);
            if (dib is not null)
            {
                return EncodePng(dib);
            }

            // ③ WPF 的 Bitmap 格式兜底（部分应用只放 Bitmap 不放 DIB）。
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                return EncodePng(bitmap);
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取 <c>PNG</c> 格式（可能是 <c>byte[]</c> 或 <see cref="Stream"/>，逐种尝试）。</summary>
    private static byte[]? TryReadPngFormat(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(NativeMethods.ClipboardFormatPng))
        {
            return null;
        }

        var raw = data.GetData(NativeMethods.ClipboardFormatPng);

        switch (raw)
        {
            case byte[] bytes when bytes.Length > 0:
                return bytes;

            case Stream stream:
                using (stream)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return memory.Length > 0 ? memory.ToArray() : null;
                }

            case null:
                return null;

            default:
                // 格式名已注册但载荷类型未知：不猜，交给 DIB 回退路径。
                return null;
        }
    }

    /// <summary>
    /// 取 <c>CF_DIB</c> 并转为 <see cref="BitmapSource"/>。
    /// ⚠️ 转换过程**不关闭**传入的流（WPF 可能延迟读取），因此这里复制到独立内存流。
    /// </summary>
    private static BitmapSource? TryReadDibFormat(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(NativeMethods.ClipboardFormatDib))
        {
            return null;
        }

        var raw = data.GetData(NativeMethods.ClipboardFormatDib);

        switch (raw)
        {
            case Stream stream:
                using (stream)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    memory.Position = 0;
                    return DecodeDib(memory);
                }

            case byte[] bytes when bytes.Length > 0:
                using (var memory = new MemoryStream(bytes, writable: false))
                {
                    return DecodeDib(memory);
                }

            case null:
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 解码 DIB。<c>CF_DIB</c> **不含</c> BITMAPFILEHEADER，
    /// 因此先尝试 <see cref="BitmapFrame"/> 直接解码，失败再补 14 字节文件头重试
    /// （不同来源的 CF_DIB 有的带头有的不带）。
    /// </summary>
    private static BitmapSource? DecodeDib(Stream dibStream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(dibStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception)
        {
            // 落到「补 BMP 文件头」的下一条路径。
        }

        if (!dibStream.CanSeek)
        {
            return null;
        }

        dibStream.Position = 0;
        using var raw = new MemoryStream();
        dibStream.CopyTo(raw);
        return DecodeDibWithFileHeader(raw.ToArray());
    }

    /// <summary>给裸 DIB 补 14 字节 <c>BITMAPFILEHEADER</c> 后用 WPF 解码（等价于构造内存 BMP）。</summary>
    private static BitmapSource? DecodeDibWithFileHeader(byte[] dib)
    {
        if (dib.Length < 14)
        {
            return null;
        }

        // BITMAPINFOHEADER 的 biSize 位于偏移 0（4 字节小端）。
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length)
        {
            return null;
        }

        // biBitCount 位于偏移 14（4 字节小端）之后……这里只需文件头偏移：
        // bfOffBits = 14 + biSize + 调色板大小。对常见 24/32 位无调色板图像，调色板为 0。
        var paletteEntries = 0;
        if (headerSize >= 40)
        {
            var bitCount = BitConverter.ToInt16(dib, 14);
            var clrUsed = BitConverter.ToInt32(dib, 32);
            if (clrUsed != 0)
            {
                paletteEntries = clrUsed;
            }
            else if (bitCount <= 8)
            {
                paletteEntries = 1 << bitCount;
            }
        }

        var offsetBits = 14 + headerSize + (paletteEntries * 4);
        if (offsetBits > dib.Length)
        {
            return null;
        }

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.TryWriteBytes(file.AsSpan(2, 4), 14 + dib.Length);
        BitConverter.TryWriteBytes(file.AsSpan(10, 4), offsetBits);
        dib.CopyTo(file, 14);

        try
        {
            using var memory = new MemoryStream(file, writable: false);
            var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把位图编码为 PNG 字节（FR-W-IMG-8 要求 PNG 编码后作为附件）。</summary>
    private static byte[]? EncodePng(BitmapSource source)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var memory = new MemoryStream();
            encoder.Save(memory);
            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
