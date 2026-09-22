using System.Globalization;
using System.Text.RegularExpressions;
using TKSDesktop.Contracts;

namespace TKSDesktop.Core.Services.Delivery;

/// <summary>拆分后的一段气泡。</summary>
/// <param name="MessageId">主键 <c>{finalMessageId}_{index}</c>（位于 <see cref="Index"/>）。</param>
/// <param name="Index">段序号，从 0 起，**只对非空段落递增**。</param>
/// <param name="Content">段落正文（已 trim，非空）。</param>
/// <param name="Timestamp"><c>{finalTimestamp} + {index}</c>。</param>
public sealed record MessageSegment(string MessageId, int Index, string Content, long Timestamp);

/// <summary>
/// 多气泡拆分（PRD FR-W-CHAT-6 / FR-W-SYNC-5）。
///
/// <para><b>规则</b>：Bot 最终内容按 <c>\n</c> 分割 → 每段 <c>Trim()</c> → 过滤空段（含仅空白行）→
/// 每段一条独立消息，主键 <c>{finalMessageId}_{index}</c>，<c>timestamp = finalTimestamp + index</c>。</para>
///
/// <para><b>为什么必须是唯一实现</b>：实时流式 <c>done</c> 链路与历史同步链路若各写一份拆分逻辑，
/// 同一条回复会以「两种形态」（有无 <c>_{index}</c> 后缀 / 段数不同）重复入库，
/// 且 <c>delivered_bot_messages</c> 去重表会失效（FR-SYNC-5 / FR-INT-12 / EDGE-W-21）。
/// 因此两条链路**都**调用本类。</para>
///
/// <para><b>纯函数</b>：无副作用、无 I/O、不读时间 —— 同样的输入必然得到同样的输出，便于单测。</para>
///
/// <para>⚠️ <b>与 <c>MessageRepository.SplitBotContent</c> 的已知差异</b>：数据层那份实现为
/// 「单行时保留原始 <c>messageId</c>」加了特例（对齐 Linux 端）。本实现严格按 PRD 原文
/// **始终**产出 <c>{finalMessageId}_{index}</c>。两者对多行输入一致，对单行输入不一致，
/// 需由主控统一（见 handoff 的 KNOWN RISKS）。</para>
///
/// <para>换行兼容：CRLF 输入会因 <c>Trim()</c> 去掉行尾 <c>\r</c>，因此 <c>\r\n</c> 与 <c>\n</c>
/// 拆分结果一致；单行内容中间的 <c>\r</c> 不会被误删。</para>
/// </summary>
public static class MessageSplitter
{
    /// <summary>用于拆分的换行字符。</summary>
    private const char LineSeparator = '\n';

    /// <summary>
    /// 拆分 Bot 最终内容。
    /// </summary>
    /// <param name="finalMessageId">服务端下发的消息主键（<c>done.payload.messageId</c>）。</param>
    /// <param name="content">最终内容（<c>finalContent</c> 或历史行 <c>content</c>）。</param>
    /// <param name="finalTimestamp">该消息的基准时间戳（毫秒）。</param>
    /// <returns>非空段落列表；全部为空时返回空列表（调用方**不得**落空内容气泡）。</returns>
    public static IReadOnlyList<MessageSegment> Split(
        string finalMessageId,
        string? content,
        long finalTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalMessageId);

        var segments = new List<MessageSegment>();
        if (string.IsNullOrEmpty(content))
        {
            return segments;
        }

        var index = 0;
        foreach (var rawLine in content.Split(LineSeparator))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                // 空行（含仅空白行）被过滤，且**不占用 index**。
                continue;
            }

            segments.Add(new MessageSegment(
                string.Create(CultureInfo.InvariantCulture, $"{finalMessageId}_{index}"),
                index,
                line,
                finalTimestamp + index));

            index += 1;
        }

        return segments;
    }
}

/// <summary>
/// 历史文本清洗（PRD FR-W-SYNC-9 / C-5）。
///
/// 纯逻辑，供 <c>SyncService</c> 与渲染前的规整共用。
/// </summary>
public static partial class HistoryText
{
    /// <summary>
    /// 剥离服务端写入的 <c>【MM-DD HH:MM】</c> 前缀（<c>with_history_timestamp</c> 仅用于喂模型）。
    /// 前缀前允许空白；前缀后允许空白一并去掉。不匹配时原样返回（**不**做任何其他清洗）。
    /// </summary>
    public static string StripTimestampPrefix(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return HistoryTimestampPrefix().Replace(content, string.Empty, count: 1);
    }

    /// <summary>
    /// 内容是否为「新的一天」历史分隔标记（C-5）。
    /// ⚠️ 复用冻结契约的 <see cref="ProtocolConstants.IsHistorySeparator"/>，**不得**另写比较逻辑。
    /// </summary>
    public static bool IsHistorySeparator(string? content) => ProtocolConstants.IsHistorySeparator(content);

    /// <summary>
    /// `【08-22 17:22】` 形态的前缀（FR-W-SYNC-9）。`count: 1` 保证只剥离开头一处。
    /// ⚠️ 模式字面量取自 <see cref="ProtocolConstants.HistoryTimestampPrefixPattern"/>
    /// （服务端协议常量，集中登记在契约层）。
    /// </summary>
    [GeneratedRegex(ProtocolConstants.HistoryTimestampPrefixPattern, RegexOptions.CultureInvariant)]
    private static partial Regex HistoryTimestampPrefix();
}
