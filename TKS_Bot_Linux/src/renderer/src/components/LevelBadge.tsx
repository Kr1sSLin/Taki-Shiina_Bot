/**
 * 等级徽章与升级进度条（FR-LV-1 / FR-LV-2 / FR-LV-4）。
 *
 * 视觉依据 `视觉资产/熊猫图像.txt` 的配色（见 `shared/levels.ts`）；
 * 「传奇熊猫」使用七彩动态特效（FR-LV-4），在 `prefers-reduced-motion` 下退化为静态渐变（NFR-11）。
 *
 * ⚠️ 徽章为 CSS 实现（不依赖美术资源）；服务端 `iconUrl` 到位后可直接替换为图片。
 */

import { LEVEL_VISUAL_BY_CODE, DEFAULT_LEVEL_VISUAL } from '@shared/levels'
import type { ResolvedLevel } from '../lib/level'

export function LevelBadge({
  level,
  size = 72,
  showEmoji = true
}: {
  level: Pick<ResolvedLevel, 'code' | 'emoji' | 'gradient' | 'accent' | 'animated' | 'isDefault'>
  size?: number
  showEmoji?: boolean
}): JSX.Element {
  const visual = LEVEL_VISUAL_BY_CODE[level.code] ?? DEFAULT_LEVEL_VISUAL
  const emoji = level.emoji || visual.emoji
  return (
    <span
      className={[
        'level-badge',
        level.animated ? 'is-legendary' : '',
        level.isDefault ? 'is-default' : ''
      ]
        .filter(Boolean)
        .join(' ')}
      style={{
        width: size,
        height: size,
        fontSize: size * 0.42,
        background: `linear-gradient(140deg, ${level.gradient[0]}, ${level.gradient[1]})`,
        boxShadow: `0 6px 20px -6px ${level.accent}`
      }}
      role="img"
      aria-label={level.code}
    >
      <span className="level-badge-ring" />
      {showEmoji ? <span className="level-badge-emoji">{emoji}</span> : null}
    </span>
  )
}

/**
 * 升级进度条（FR-LV-1）。
 *
 * FR-PROG-3：比例仅用于展示；`currentDays` / `targetDays` 全部来自服务端。
 */
export function LevelProgressBar({
  ratio,
  accent,
  animated,
  label
}: {
  ratio: number
  accent: string
  animated?: boolean
  label?: string
}): JSX.Element {
  const pct = Math.round(Math.min(1, Math.max(0, ratio)) * 100)
  return (
    <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct} aria-label={label ?? 'progress'}>
      <div
        className={['progress-fill', animated ? 'is-legendary' : ''].filter(Boolean).join(' ')}
        style={{ width: `${pct}%`, background: animated ? undefined : accent }}
      />
    </div>
  )
}

/** 迷你等级徽章（列表/流水行内使用）。 */
export function LevelChip({ level, name }: { level: ResolvedLevel; name?: string }): JSX.Element {
  return (
    <span
      className="level-chip"
      style={{ background: `linear-gradient(120deg, ${level.gradient[0]}33, ${level.gradient[1]}22)`, borderColor: `${level.accent}55` }}
    >
      <span aria-hidden="true">{level.emoji}</span>
      <span>{name ?? level.name}</span>
    </span>
  )
}
