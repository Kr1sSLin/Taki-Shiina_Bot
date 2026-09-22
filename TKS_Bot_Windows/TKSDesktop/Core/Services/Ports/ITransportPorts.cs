using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Services.Ports;

/// <summary>
/// 端口调用结果（强类型，**不把错误编码进 message**，对齐 FR-W-ARCH-2）。
/// </summary>
/// <typeparam name="T">成功时的值类型。</typeparam>
/// <param name="Success">是否成功。</param>
/// <param name="Value">成功时的值；失败时 <c>default</c>。</param>
/// <param name="ErrorCode">服务端 / 客户端错误码（可能为 <c>null</c>）。</param>
/// <param name="I18nKey">必然是**已登记**的界面文案 key（未知码回落 <c>error.unknown</c>）。</param>
public sealed record PortCall<T>(
    bool Success,
    T? Value,
    string? ErrorCode,
    string I18nKey)
{
    /// <summary>成功。</summary>
    public static PortCall<T> Ok(T value) => new(true, value, null, ErrorCatalog.UnknownI18nKey);

    /// <summary>失败。</summary>
    public static PortCall<T> Fail(string? errorCode, string i18nKey) => new(false, default, errorCode, i18nKey);
}

/// <summary>
/// WS 长连接端口（窄接口）。
///
/// 只暴露对话核心用到的能力：当前连接快照、已解析帧、发 `chat.message`、请求重连、
/// 置全量同步标志。心跳 / 退避 / 4001 处理 / 帧解析全部由连接层负责，本层**不重复实现**
/// （FR-W-CONN-1..4 / 陷阱 1、2、6）。
/// </summary>
public interface IWssPort
{
    /// <summary>当前连接状态快照（用于 <c>IChatService.State</c>）。</summary>
    ConnectionSnapshot Current { get; }

    /// <summary>是否已连接且可发送。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 下一次建连是否需要 `since=0` 全量同步（由连接层维护）。
    /// </summary>
    bool NeedsFullSync { get; }

    /// <summary>连接状态变化。</summary>
    event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    /// <summary>已识别的服务端帧（未知 type / 非法 JSON 不会到达此处）。</summary>
    event EventHandler<WsServerFrame>? FrameReceived;

    /// <summary>
    /// 发送 `chat.message`。⚠️ <paramref name="requestId"/> 必须落在**帧的顶层**（陷阱 1），
    /// 由连接层负责保证。返回是否成功入队。
    /// </summary>
    bool SendChatMessage(string requestId, string? content, IReadOnlyList<ChatImagePayloadDto>? images);

    /// <summary>
    /// 请求（重新）建立连接。<paramref name="manual"/> 为 <c>true</c> 表示用户手动重连，
    /// 连接层据此置 `manualReconnectPendingFullSync`（FR-W-CONN-6）。
    /// </summary>
    Task ReconnectAsync(bool manual, CancellationToken cancellationToken = default);

    /// <summary>置「下次建连需要全量同步」标志（FR-W-CONN-6 / FR-W-SYNC-4）。</summary>
    void MarkFullSyncRequired();
}

/// <summary>
/// REST 端口（窄接口）：只声明同步链路需要的两个 GET（§5.2）。
/// 信封解包 / 401 续签互斥 / trace / 超时分级由 <c>RestClient</c> 负责。
/// </summary>
public interface IRestPort
{
    Task<PortCall<RestChatDataDto>> SendChatAsync(RestChatRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// `GET /chat/history?since={since}&amp;limit={limit}`。
    /// <paramref name="since"/> 由调用方按 `max(本地最大 timestamp, 游标)` 计算（FR-W-SYNC-3）。
    /// </summary>
    Task<PortCall<IReadOnlyList<TimelineItemDto>>> GetChatHistoryAsync(
        long since,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>`GET /memory/facts?since={since}&amp;limit={limit}`（FR-W-SYNC-7）。</summary>
    Task<PortCall<IReadOnlyList<UserFactDto>>> GetMemoryFactsAsync(
        long since,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件系统端口（窄接口）。
///
/// 存在的理由：附件写入前的**磁盘余量检查**（EDGE-W-15）、私有目录复制（FR-W-IMG-5）
/// 与按天清理（FR-W-SET-11）都必须可在单测中注入确定性行为，且 <c>Core</c> 不应散落
/// 直接的文件 API 调用。实现位于平台 / 适配层。
/// </summary>
public interface IFileStoragePort
{
    /// <summary>文件是否存在。</summary>
    bool FileExists(string path);

    /// <summary>目录是否存在。</summary>
    bool DirectoryExists(string path);

    /// <summary>创建目录（已存在时幂等）。</summary>
    void CreateDirectory(string path);

    /// <summary>文件字节数；文件不存在时为 0。</summary>
    long GetFileSize(string path);

    /// <summary>读取全部字节；读取失败（不存在 / 被占用 / 无权限）返回 <c>null</c>，**不抛错**。</summary>
    byte[]? TryReadAllBytes(string path);

    /// <summary>写入全部字节（覆盖）。</summary>
    void WriteAllBytes(string path, byte[] content);

    /// <summary>复制文件（覆盖目标）。</summary>
    void CopyFile(string sourcePath, string destinationPath);

    /// <summary>删除文件；不存在或删除失败返回 <c>false</c>（**不抛错**）。</summary>
    bool TryDeleteFile(string path);

    /// <summary>
    /// 给定路径所在卷的可用空闲字节数。
    /// 无法探测时返回 <see cref="long.MaxValue"/>（**不阻塞用户**，与 Linux 端语义一致）。
    /// </summary>
    long GetAvailableFreeSpaceBytes(string directory);

    /// <summary>枚举目录下的文件名（不含子目录）；目录不存在时返回空列表。</summary>
    IReadOnlyList<string> EnumerateFileNames(string directory);
}

/// <summary>
/// 图片探测端口（窄接口）。
///
/// ⚠️ <c>Core</c> **不得**引用 <c>System.Windows.Media.Imaging</c>（NFR-W-14 / V-W-S1），
/// 也**不得**引用 <c>System.Drawing</c>（会把 <c>Core</c> 绑到 GDI+）。
/// 因此尺寸探测与 MIME 嗅探由平台层实现本接口（FR-W-IMG-2 / FR-W-IMG-6）。
/// </summary>
public interface IImageProbe
{
    /// <summary>
    /// 读取像素尺寸。<paramref name="path"/> 不是可解码图片时返回 <c>null</c>
    /// （**不抛错**：尺寸缺失不影响发送，只是元数据为空）。
    /// </summary>
    (int Width, int Height)? TryReadDimensions(string path, string mimeType);

    /// <summary>
    /// 按**内容**嗅探 MIME（`image/jpeg` / `image/png`）。
    /// 无法判定时返回 <c>null</c>，调用方回退到扩展名判定。
    /// </summary>
    string? TryDetectMimeType(string path);
}
