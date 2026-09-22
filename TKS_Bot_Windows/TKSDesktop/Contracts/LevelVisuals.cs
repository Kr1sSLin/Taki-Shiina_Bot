namespace TKSDesktop.Contracts;

/// <summary>
/// 等级视觉规范（PRD §5.5 C-1 / §6.10 表）。
///
/// ⚠️ 契约：配色与 emoji **必须与另外两端逐值一致**（V-W-B13 逐值验收）。
/// ⚠️ 阈值（<see cref="LevelVisual.FallbackThresholdDays"/>）**仅用于兜底与离线渲染**；
///    业务判定一律以服务端 `GET /level/config` 的 `threshold_days` 为准（FR-W-LV-2 / W-P2）。
/// ⚠️ <see cref="LevelVisual.FallbackName"/> 仅在服务端 `level_name` 为空时使用；
///    `levelCode = NONE` 时服务端返回空 `levelName` + `isDefaultLevel = true`，必须走中性兜底（FR-W-LV-3）。
/// </summary>
public sealed record LevelVisual(
    string Code,
    string FallbackName,
    string FallbackNameEn,
    string Emoji,
    string GradientFrom,
    string GradientTo,
    string Accent,
    int FallbackThresholdDays,
    bool Animated = false);

public static class LevelVisuals
{
    /// <summary>`levelCode = NONE` 的中性兜底 code。</summary>
    public const string DefaultLevelCode = "NONE";

    /// <summary>7 档等级（自低到高，与 `/level/config` 默认 `sort_order` 一致）。</summary>
    public static readonly IReadOnlyList<LevelVisual> All =
    [
        new("PANDA_LV1", "初生熊猫", "Newborn Panda", "🥚", "#D9D9D9", "#BFC3C7", "#B9BDC1", 3),
        new("PANDA_LV2", "好奇宝宝", "Curious Cub", "🌱", "#C9E7A8", "#8FCB6B", "#8FCB6B", 7),
        new("PANDA_LV3", "竹林新秀", "Bamboo Rookie", "🍃", "#8FDCA0", "#37A65C", "#37A65C", 15),
        new("PANDA_LV4", "黑白骑士", "Monochrome Knight", "🌊", "#3E6FA8", "#1B3B63", "#2C5A8C", 30),
        new("PANDA_LV5", "功夫大师", "Kung Fu Master", "✨", "#F7D774", "#D8A32B", "#D8A32B", 60),
        new("PANDA_LV6", "熊猫长老", "Panda Elder", "💜", "#B48CE0", "#7A4FBF", "#8B5CD6", 100),
        new("PANDA_LV7", "传奇熊猫", "Legendary Panda", "🌈", "#FF6B6B", "#FFD93D", "#FF8A5B", 200, Animated: true),
    ];

    /// <summary>
    /// 默认态视觉（`levelCode = NONE`）。PRD FR-W-LV-3：不设专属文案，用中性兜底。
    /// </summary>
    public static readonly LevelVisual Default =
        new(DefaultLevelCode, "刚刚开始", "Just Started", "🐾", "#CFD4DA", "#A8AFB8", "#A8AFB8", 0);

    private static readonly Dictionary<string, LevelVisual> ByCode =
        All.ToDictionary(static v => v.Code, StringComparer.Ordinal);

    /// <summary>
    /// 按 <paramref name="levelCode"/> 解析视觉；未知/空 code 与 `NONE` 一律回退到
    /// <see cref="Default"/>（不抛错——NFR-W-12 要求容忍未知内容）。
    ///
    /// ⚠️ 必须防御空 `levelCode`：另一端的缺陷正是「`level_code` 取成空串导致列表 key 重复而崩溃」
    ///    （EDGE-W-24 / FR-W-PROTO-2）。
    /// </summary>
    public static LevelVisual Resolve(string? levelCode)
    {
        if (string.IsNullOrWhiteSpace(levelCode))
        {
            return Default;
        }

        return ByCode.TryGetValue(levelCode, out var visual) ? visual : Default;
    }

    /// <summary>
    /// 解析显示用称号：优先服务端下发的 <paramref name="serverLevelName"/>，
    /// 为空时回退到本地兜底称号（FR-W-LV-3：`NONE` 不得渲染空标题）。
    /// </summary>
    public static string ResolveDisplayName(string? levelCode, string? serverLevelName)
    {
        if (!string.IsNullOrWhiteSpace(serverLevelName))
        {
            return serverLevelName;
        }

        return Resolve(levelCode).FallbackName;
    }
}
