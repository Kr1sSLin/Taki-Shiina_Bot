using Microsoft.Extensions.Logging;
using TKSDesktop.Contracts;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services.Ports;

namespace TKSDesktop.Core.Services.Media;

/// <summary>
/// 图片 / 附件服务（PRD FR-W-IMG-1..11、FR-W-SET-11、EDGE-W-15）。
///
/// <list type="bullet">
///   <item><b>FR-W-IMG-2</b>：仅 <c>image/jpeg</c> / <c>image/png</c>，单张 ≤
///         <see cref="ProtocolConstants.MaxImageBytes"/>（20MB），条数 ≤
///         <see cref="ProtocolConstants.MaxImageCount"/>（3）—— 全部**在本地拦截**，绝不发往服务端。</item>
///   <item><b>FR-W-IMG-11</b>：发送前的**总量**校验（base64 后总长 ≤
///         <see cref="ProtocolConstants.MaxTotalAttachmentBytes"/>，24MB）防 nginx 413。</item>
///   <item><b>FR-W-IMG-5</b>：图片**先复制进** <see cref="IPaths.AttachmentsDir"/> 再记 <c>localPath</c>，
///         文件名为 <c>{guid}{ext}</c>；用户移动 / 删除原文件不影响历史消息。</item>
///   <item><b>EDGE-W-15</b>：写入前检查可用空间，低于
///         <see cref="ProtocolConstants.AttachmentFreeSpaceReserveBytes"/>（64MB）则拒绝。</item>
/// </list>
///
/// <para>⚠️ 本类**不得抛异常**（NFR-W-12）：所有失败都收敛为
/// <see cref="AttachmentDraftResult"/> 的失败值 + i18n key。</para>
///
/// <para>⚠️ 尺寸与 MIME 探测一律经 <see cref="IImageProbe"/>（NFR-W-14 / V-W-S1：
/// <c>Core</c> 不得引用 <c>System.Windows.*</c> 或 <c>System.Drawing</c>）。</para>
/// </summary>
public sealed class MediaService : IMediaService
{
    /// <summary>base64 编码膨胀后的字节数（4 × ⌈n / 3⌉，含填充）。</summary>
    private const int Base64Group = 3;
    private const int Base64CharsPerGroup = 4;

    private static readonly string[] JpegMime = ["image/jpeg", "image/pjpeg"];
    private static readonly string[] PngMime = ["image/png"];

    private readonly IAttachmentPort _attachments;
    private readonly IFileStoragePort _files;
    private readonly IImageProbe _probe;
    private readonly IClipboardImage _clipboard;
    private readonly IPaths _paths;
    private readonly ILogger<MediaService> _logger;

    /// <summary>草稿集合的互斥门（⚠️ .NET 8 无 <c>System.Threading.Lock</c>，用 object + lock）。</summary>
    private readonly object _gate = new();

    private readonly List<AttachmentDraft> _drafts = [];

    /// <summary>构造（只经窄接口，不依赖任何 Data / Network / Auth 具体类型）。</summary>
    /// <param name="attachments">附件持久化端口（草稿登记 / 删除 / 统计，§9.2 <c>message_id</c> 允许 NULL）。</param>
    /// <param name="files">文件系统端口（私有目录复制、余量探测、按天清理）。</param>
    /// <param name="probe">图片探测端口（按内容嗅探 MIME + 读取像素尺寸）。</param>
    /// <param name="clipboard">剪贴板图片端口（FR-W-IMG-8；平台层实现，Core 只见接口）。</param>
    /// <param name="paths">路径解析（附件私有目录）。</param>
    /// <param name="logger">日志（**不含**图片字节 / base64）。</param>
    public MediaService(
        IAttachmentPort attachments,
        IFileStoragePort files,
        IImageProbe probe,
        IClipboardImage clipboard,
        IPaths paths,
        ILogger<MediaService> logger)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _attachments = attachments;
        _files = files;
        _probe = probe;
        _clipboard = clipboard;
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<AttachmentDraft> Drafts
    {
        get
        {
            lock (_gate)
            {
                return _drafts.ToArray();
            }
        }
    }

    /* ===================================================================== */
    /* FR-W-IMG-1 / 2 / 5 / 8 / 11 / EDGE-W-15：添加                        */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task<AttachmentDraftResult> AddFromFileAsync(string sourcePath, CancellationToken ct = default)
    {
        // 张数上限在**读取文件之前**判定（FR-W-IMG-2：本地拦截，不发往服务端）。
        if (RejectByCount() is { } tooMany)
        {
            return tooMany;
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Failure("image.notFound");
        }

        byte[]? bytes;
        try
        {
            if (!_files.FileExists(sourcePath))
            {
                return Failure("image.notFound");
            }

            bytes = _files.TryReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "附件读取失败，按「文件不存在」处理");
            return Failure("image.notFound");
        }

        if (bytes is null || bytes.Length == 0)
        {
            // 不存在 / 被占用 / 无权限 → 端口约定返回 null（不抛错）。
            return Failure("image.notFound");
        }

        var mimeType = ResolveMimeType(sourcePath, bytes);
        return await PersistAsync(mimeType, bytes, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AttachmentDraftResult> AddFromClipboardAsync(CancellationToken ct = default)
    {
        if (RejectByCount() is { } tooMany)
        {
            return tooMany;
        }

        byte[]? png;
        try
        {
            png = _clipboard.TryReadPng();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 剪贴板被其他进程占用等：**必须**给出明确提示，不得静默无反应（FR-W-IMG-8）。
            _logger.LogWarning(ex, "剪贴板读取失败，按「无图片」处理");
            return Failure("image.paste.empty");
        }

        if (png is null || png.Length == 0)
        {
            return Failure("image.paste.empty");
        }

        // 剪贴板一律以 PNG 编码（FR-W-IMG-8：CF_DIB 无 alpha 时转 PNG）。
        return await PersistAsync("image/png", png, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 校验（体积 / 类型 / 总量 / 余量）→ 复制进私有目录 → 登记草稿。
    /// 任一步失败都返回失败结果且**不留残留文件**。
    /// </summary>
    private async Task<AttachmentDraftResult> PersistAsync(string? mimeType, byte[] bytes, CancellationToken ct)
    {
        if (mimeType is null || !IsAllowedMime(mimeType))
        {
            return Failure("image.invalidType");
        }

        var fileSize = (long)bytes.Length;

        // FR-W-IMG-2：单张 ≤ 20MB。
        if (fileSize > ProtocolConstants.MaxImageBytes)
        {
            return Failure("image.tooLarge");
        }

        // FR-W-IMG-11：base64 后的**总量** ≤ 24MB（413 防御）。
        if (ExceedsTotalBudget(fileSize))
        {
            return Failure("image.totalTooLarge");
        }

        // EDGE-W-15：可用空间必须大于「预留 64MB + 本次新增文件」。
        var available = _files.GetAvailableFreeSpaceBytes(_paths.AttachmentsDir);
        if (available < ProtocolConstants.AttachmentFreeSpaceReserveBytes + fileSize)
        {
            _logger.LogWarning(
                "附件写入前磁盘余量不足，拒绝添加 available={Available} required={Required} reserve={Reserve}",
                available,
                fileSize,
                ProtocolConstants.AttachmentFreeSpaceReserveBytes);
            return Failure("image.diskFull");
        }

        string? destination = null;
        try
        {
            // FR-W-IMG-5：复制到应用私有目录后再记录 localPath。
            _files.CreateDirectory(_paths.AttachmentsDir);
            destination = Path.Combine(_paths.AttachmentsDir, BuildFileName(mimeType));

            if (TryCopy(destination, bytes) is { } writeFailure)
            {
                return writeFailure;
            }

            var dimensions = TryReadDimensions(destination, mimeType);
            var draft = await _attachments
                .InsertDraftAsync(mimeType, destination, fileSize, dimensions?.Width, dimensions?.Height, ct)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _drafts.Add(draft);
            }

            _logger.LogInformation(
                "附件草稿已登记 attachmentId={AttachmentId} mime={Mime} size={Size} hasDimensions={HasDimensions}",
                draft.AttachmentId,
                mimeType,
                fileSize,
                dimensions is not null);

            return new AttachmentDraftResult(true, draft, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 登记失败不得留下孤儿文件（磁盘与 DB 必须一致）。
            CleanupFile(destination);
            _logger.LogWarning(ex, "附件草稿登记失败，已回滚磁盘文件 mime={Mime} size={Size}", mimeType, fileSize);
            return Failure(ErrorCatalog.UnknownI18nKey);
        }
    }

    /// <summary>
    /// 写入私有目录；失败返回失败结果（<c>null</c> 表示成功）。
    /// 端口只提供 <c>WriteAllBytes</c>（源路径读取已在上游完成），因此统一走字节写入。
    /// </summary>
    private AttachmentDraftResult? TryCopy(string destination, byte[] bytes)
    {
        try
        {
            _files.WriteAllBytes(destination, bytes);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "附件写入私有目录失败");
            return Failure("image.diskFull");
        }
    }

    /* ===================================================================== */
    /* FR-W-IMG-3：草稿增删清恢复                                             */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task RemoveDraftAsync(string attachmentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            return;
        }

        AttachmentDraft? draft;
        lock (_gate)
        {
            draft = _drafts.FirstOrDefault(d => string.Equals(d.AttachmentId, attachmentId, StringComparison.Ordinal));
        }

        try
        {
            // 先删记录再删磁盘文件：即使磁盘删除失败，UI 也不再展示该草稿。
            await _attachments.DeleteAsync(attachmentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "草稿附件记录删除失败 attachmentId={AttachmentId}", attachmentId);
        }

        if (draft is not null)
        {
            CleanupFile(draft.LocalPath);

            lock (_gate)
            {
                _drafts.RemoveAll(d => string.Equals(d.AttachmentId, attachmentId, StringComparison.Ordinal));
            }
        }
    }

    /// <inheritdoc />
    public async Task ClearDraftsAsync(CancellationToken ct = default)
    {
        // 发送成功后这些内存草稿可能已绑定消息。只清理仍为 NULL message_id 的记录，
        // 已绑定附件及私有文件必须留给历史和重发使用。
        var unbound = (await _attachments.ListDraftsAsync(ct).ConfigureAwait(false))
            .Select(static draft => draft.AttachmentId).ToHashSet(StringComparer.Ordinal);
        AttachmentDraft[] snapshot;
        lock (_gate)
        {
            snapshot = _drafts.ToArray();
            _drafts.Clear();
        }

        foreach (var draft in snapshot)
        {
            if (!unbound.Contains(draft.AttachmentId))
            {
                continue;
            }

            try
            {
                await _attachments.DeleteAsync(draft.AttachmentId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "清空草稿时记录删除失败 attachmentId={AttachmentId}", draft.AttachmentId);
            }

            CleanupFile(draft.LocalPath);
        }
    }

    /// <inheritdoc />
    public async Task RestoreDraftsAsync(CancellationToken ct = default)
    {
        try
        {
            var stored = await _attachments.ListDraftsAsync(ct).ConfigureAwait(false);
            var restored = new List<AttachmentDraft>(Math.Min(stored.Count, ProtocolConstants.MaxImageCount));

            foreach (var draft in stored)
            {
                if (restored.Count >= ProtocolConstants.MaxImageCount)
                {
                    // 超出单条消息上限的存量草稿保留在库中，但不再装入待发送列表（FR-W-IMG-2）。
                    _logger.LogWarning(
                        "存量草稿超过单条消息上限，多余项不装入待发送列表 total={Total} cap={Cap}",
                        stored.Count,
                        ProtocolConstants.MaxImageCount);
                    break;
                }

                if (string.IsNullOrWhiteSpace(draft.LocalPath) || !_files.FileExists(draft.LocalPath))
                {
                    // 文件已丢失（用户手工清理 / 换机）：登记为孤儿并清掉记录，避免渲染破图（EDGE-W-23）。
                    _logger.LogWarning("草稿附件文件缺失，清理记录 attachmentId={AttachmentId}", draft.AttachmentId);
                    await _attachments.DeleteAsync(draft.AttachmentId, ct).ConfigureAwait(false);
                    continue;
                }

                restored.Add(draft);
            }

            lock (_gate)
            {
                _drafts.Clear();
                _drafts.AddRange(restored);
            }

            _logger.LogInformation("草稿附件恢复完成 restored={Restored} stored={Stored}", restored.Count, stored.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 恢复失败不得让聊天页起不来（NFR-W-12）。
            _logger.LogWarning(ex, "草稿附件恢复失败，按空草稿继续");
        }
    }

    /* ===================================================================== */
    /* FR-W-SET-11：占用统计与按天清理                                        */
    /* ===================================================================== */

    /// <inheritdoc />
    public async Task<long> GetAttachmentsSizeAsync(CancellationToken ct = default)
    {
        try
        {
            return await _attachments.TotalSizeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "附件占用统计失败，返回 0");
            return 0L;
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanupAttachmentsAsync(int olderThanDays, CancellationToken ct = default)
    {
        if (olderThanDays < 0)
        {
            return 0;
        }

        try
        {
            var (deletedCount, paths) = await _attachments
                .DeleteOlderThanAsync(olderThanDays, ct)
                .ConfigureAwait(false);

            foreach (var path in paths)
            {
                CleanupFile(path);
            }

            _logger.LogInformation(
                "本地附件清理完成 olderThanDays={Days} deletedRecords={Deleted} deletedFiles={Files}",
                olderThanDays,
                deletedCount,
                paths.Count);

            return deletedCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "本地附件清理失败 olderThanDays={Days}", olderThanDays);
            return 0;
        }
    }

    /* ===================================================================== */
    /* 纯逻辑（可单测）                                                       */
    /* ===================================================================== */

    /// <summary>
    /// FR-W-IMG-11：把候选文件与**当前全部草稿**的 base64 后总长与
    /// <see cref="ProtocolConstants.MaxTotalAttachmentBytes"/>（24MB）比较。
    /// </summary>
    /// <param name="candidateBytes">候选文件原始字节数。</param>
    /// <returns>超限返回 <c>true</c>（调用方据此返回 <c>image.totalTooLarge</c>）。</returns>
    public bool ExceedsTotalBudget(long candidateBytes)
    {
        if (candidateBytes <= 0)
        {
            return false;
        }

        long total = Base64Length(candidateBytes);

        lock (_gate)
        {
            foreach (var draft in _drafts)
            {
                total += Base64Length(draft.FileSize);
            }
        }

        return total > ProtocolConstants.MaxTotalAttachmentBytes;
    }

    /// <summary>
    /// base64 编码后的字符数（= 编码后字节数）：<c>4 × ⌈n / 3⌉</c>。
    /// 与 <c>MaxTotalAttachmentBytes</c> 的口径一致（该常量已为「base64 膨胀 + JSON 包装」预留余量）。
    /// </summary>
    public static long Base64Length(long byteCount)
        => byteCount <= 0
            ? 0L
            : ((byteCount + Base64Group - 1) / Base64Group) * Base64CharsPerGroup;

    /// <summary>分支判定：MIME 是否在契约白名单内（FR-W-IMG-2）。</summary>
    public static bool IsAllowedMime(string? mimeType)
        => !string.IsNullOrWhiteSpace(mimeType)
           && ProtocolConstants.AllowedImageMime.Contains(mimeType.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读取像素尺寸；<see cref="IImageProbe.TryReadDimensions"/> 约定不抛错，
    /// 但平台实现异常不得让添加附件整体失败（尺寸只是**可选元数据** —— FR-W-IMG-6）。
    /// </summary>
    private (int Width, int Height)? TryReadDimensions(string path, string mimeType)
    {
        try
        {
            return _probe.TryReadDimensions(path, mimeType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "读取图片尺寸失败，按无尺寸元数据继续");
            return null;
        }
    }

    /* ===================================================================== */
    /* 内部                                                                */
    /* ===================================================================== */

    /// <summary>张数上限判定（FR-W-IMG-2）；未超限返回 <c>null</c>。</summary>
    private AttachmentDraftResult? RejectByCount()
    {
        lock (_gate)
        {
            return _drafts.Count >= ProtocolConstants.MaxImageCount ? Failure("image.tooMany") : null;
        }
    }

    /// <summary>
    /// MIME 解析：**优先按内容**嗅探（<see cref="IImageProbe.TryDetectMimeType"/>），
    /// 无法判定时按扩展名回退（端口约定 —— FR-W-IMG-2）。
    /// </summary>
    private string? ResolveMimeType(string path, byte[] bytes)
    {
        try
        {
            if (_probe.TryDetectMimeType(path) is { } sniffed && !string.IsNullOrWhiteSpace(sniffed))
            {
                return sniffed.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "按内容嗅探 MIME 失败，回退扩展名判定");
        }

        // 扩展名回退；同时用魔数兜底（内容嗅探不可用时仍不误判为合法图片）。
        return MatchMagic(bytes) ?? FromExtension(path);
    }

    /// <summary>按文件头魔数判定 MIME；无法判定返回 <c>null</c>。</summary>
    private static string? MatchMagic(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }

    /// <summary>按扩展名判定 MIME（仅 jpg/jpeg/png）。</summary>
    private static string? FromExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => null,
        };
    }

    /// <summary>FR-W-IMG-5：私有目录内的文件名一律 <c>{guid}{ext}</c>，不含用户输入。</summary>
    private static string BuildFileName(string mimeType)
        => string.Concat(
            Guid.NewGuid().ToString("N"),
            JpegMime.Contains(mimeType, StringComparer.OrdinalIgnoreCase) ? ".jpg"
            : PngMime.Contains(mimeType, StringComparer.OrdinalIgnoreCase) ? ".png"
            : ".img");

    /// <summary>构造失败结果（<c>ErrorI18nKey</c> 必为已登记 key）。</summary>
    private static AttachmentDraftResult Failure(string i18nKey)
        => new(false, null, i18nKey);

    /// <summary>删除磁盘文件；失败只记日志（不得让调用方失败）。</summary>
    private void CleanupFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!_files.TryDeleteFile(path))
            {
                _logger.LogWarning("附件磁盘文件删除未成功（不存在或占用）");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "附件磁盘文件删除抛错");
        }
    }
}
