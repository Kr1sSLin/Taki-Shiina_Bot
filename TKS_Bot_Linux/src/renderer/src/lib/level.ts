/**
 * 等级视觉解析（FR-LV-2 / FR-LV-3 / EDGE-L24）。
 *
 * ⚠️ 阈值与称号**以服务端为准**：
 *   - 称号优先用 `levelName`（`/points/overview`、`/level/status`）
 *   - 阈值优先用 `/level/config` 的 `levels[].threshold_days`（运营可后台改）
 *   - `LEVEL_VISUALS`（源自 `视觉资产/熊猫图像.txt`）只提供**配色与兜底**
 *
 * EDGE-L24：`/level/config` 的 `levels[]` 是 snake_case，且必须对 `level_code` 为空做防御，
 * 否则会出现重复 key 的渲染异常（Android 端曾因此闪退）。
 */

import type { LevelConfigData, LevelConfigEntry, LevelStatus } from '@shared/protocol'
import { DEFAULT_LEVEL_VISUAL, LEVEL_VISUALS, LEVEL_VISUAL_BY_CODE, type LevelVisual } from '@shared/levels'
import { t } from '../i18n'

export interface ResolvedLevel {
  code: string
  name: string
  emoji: string
  gradient: [string, string]
  accent: string
  animated: boolean
  /** `true` 表示等级为 `NONE` 或数据缺失，应使用中性兜底展示（FR-LV-3）。 */
  isDefault: boolean
  /** 阈值天数：优先服务端 `threshold_days`，否则用兜底值。 */
  thresholdDays: number | null
}

/** 服务端下发的等级定义（已按 `sort_order` 排序）。 */
export function normalizeLevels(config: LevelConfigData | null): LevelConfigEntry[] {
  if (!config?.levels) return []
  return config.levels
    // EDGE-L24 防御：`level_code` 为空一律丢弃，绝不让空字符串成为列表 key
    .filter((lv) => !!lv && typeof lv.level_code === 'string' && lv.level_code.trim().length > 0)
    .slice()
    .sort((a, b) => (a.sort_order ?? 0) - (b.sort_order ?? 0))
}

/**
 * 解析用于展示的等级视觉。
 *
 * @param levelCode 服务端 `levelCode`（`NONE` 表示默认态）
 * @param levelName 服务端 `levelName`（`NONE` 时为空字符串）
 * @param levels    服务端 `/level/config` 定义（用于取阈值与兜底称号）
 */
export function resolveLevel(
  levelCode: string | null | undefined,
  levelName: string | null | undefined,
  levels: LevelConfigEntry[] = []
): ResolvedLevel {
  const code = (levelCode ?? '').trim()

  // FR-LV-3：`levelCode=NONE` 时服务端返回空 levelName + isDefaultLevel=true，
  //         不设专属文案 → 必须提供中性兜底，不得渲染空标题或空徽章
  if (!code || code === 'NONE') {
    return {
      code: 'NONE',
      name: t('profile.level.none'),
      emoji: DEFAULT_LEVEL_VISUAL.emoji,
      gradient: DEFAULT_LEVEL_VISUAL.gradient,
      accent: DEFAULT_LEVEL_VISUAL.accent,
      animated: false,
      isDefault: true,
      thresholdDays: null
    }
  }

  const visual: LevelVisual = LEVEL_VISUAL_BY_CODE[code] ?? DEFAULT_LEVEL_VISUAL
  const serverEntry = levels.find((lv) => lv.level_code === code)

  // 称号优先服务端；为空则用本地兜底（绝不返回空串）
  const name = (levelName ?? '').trim() || serverEntry?.level_name?.trim() || visual.fallbackName

  return {
    code,
    name,
    emoji: visual.emoji,
    gradient: visual.gradient,
    accent: visual.accent,
    animated: !!visual.animated,
    isDefault: false,
    thresholdDays: serverEntry?.threshold_days ?? visual.fallbackThresholdDays
  }
}

export function resolveLevelFromStatus(status: LevelStatus | null, levels: LevelConfigEntry[] = []): ResolvedLevel {
  if (!status) return resolveLevel(null, null, levels)
  return resolveLevel(status.levelCode, status.levelName, levels)
}

/** 等级列表（等级说明页 FR-LV-2；配色按 `视觉资产/熊猫图像.txt`）。 */
export function buildLevelGuide(levels: LevelConfigEntry[]): Array<ResolvedLevel & { order: number }> {
  if (levels.length > 0) {
    return levels.map((lv) => ({
      ...resolveLevel(lv.level_code, lv.level_name, levels),
      order: lv.sort_order ?? 0
    }))
  }
  // 服务端不可用时的兜底列表（FR-LV-2：默认值与熊猫图像.txt 一致）
  return LEVEL_VISUALS.map((v, i) => ({
    ...resolveLevel(v.code, v.fallbackName, []),
    order: (i + 1) * 10
  }))
}

/**
 * 升级进度（FR-LV-1）。
 *
 * FR-PROG-3：连续天数与阈值都来自服务端；这里只做**展示用**的比例换算，
 * 不用于任何业务判定。
 */
export function levelProgress(
  status: LevelStatus | null,
  levels: LevelConfigEntry[]
): {
  currentDays: number
  targetDays: number | null
  daysToNext: number | null
  ratio: number
  nextName: string | null
  isMax: boolean
} {
  const currentDays = status?.continuousDays ?? 0
  const targetDays = status?.nextLevelThresholdDays ?? null
  const daysToNext = status?.daysToNextLevel ?? null
  const nextName = (status?.nextLevelName ?? '').trim() || null

  if (targetDays === null) {
    return { currentDays, targetDays: null, daysToNext: null, ratio: 1, nextName: null, isMax: true }
  }
  const ratio = targetDays > 0 ? Math.min(1, Math.max(0, currentDays / targetDays)) : 0
  void levels
  return { currentDays, targetDays, daysToNext, ratio, nextName, isMax: false }
}
