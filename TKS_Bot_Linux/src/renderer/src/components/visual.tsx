/**
 * 视觉还原组件（FR-UI-2 / FR-UI-3 / FR-UI-4）。
 *
 * - `DoodleBackground`：复刻 Android `ChatTopBar.kt` 的 8 种手绘涂鸦背景
 *   （star / cloud / lightning / heart / circle / moon / smile / flower），用内联 SVG 实现
 * - `LiquidGlass`：复刻 `LiquidGlass.kt` 的液态玻璃质感
 *   （CSS `backdrop-filter: blur()` + 渐变边框）
 * - `ThemeReveal`：复刻 `ThemeRevealContainer.kt` 的主题切换揭示动画
 *   （CSS `clip-path` 圆形扩散）
 *
 * NFR-11：全部动画在 `prefers-reduced-motion: reduce` 下自动禁用（见 styles/global.css）。
 */

import { useEffect, useState, type ReactNode } from 'react'

/* -------------------------------------------------------------------------- */
/* 手绘涂鸦背景（FR-UI-2，8 种图形）                                             */
/* -------------------------------------------------------------------------- */

export type DoodleKind = 'star' | 'cloud' | 'lightning' | 'heart' | 'circle' | 'moon' | 'smile' | 'flower'

export const DOODLE_KINDS: DoodleKind[] = [
  'star',
  'cloud',
  'lightning',
  'heart',
  'circle',
  'moon',
  'smile',
  'flower'
]

function DoodleShape({ kind, x, y, size, rotate }: { kind: DoodleKind; x: number; y: number; size: number; rotate: number }): JSX.Element {
  const common = {
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 2.2,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const
  }
  return (
    <g transform={`translate(${x} ${y}) rotate(${rotate}) scale(${size / 24})`} opacity={0.55}>
      {kind === 'star' && (
        <path d="M12 3.5l2.6 5.9 6.4.6-4.8 4.3 1.4 6.3L12 17.3 6.4 20.6l1.4-6.3L3 10l6.4-.6z" {...common} />
      )}
      {kind === 'cloud' && <path d="M7 17h9.5a4 4 0 0 0 .3-8 5.5 5.5 0 0 0-10.4 1.6A3.4 3.4 0 0 0 7 17z" {...common} />}
      {kind === 'lightning' && <path d="M13.5 2.5 5.5 13.5h5l-1 8 8-11h-5z" {...common} />}
      {kind === 'heart' && <path d="M12 20s-7.5-4.6-7.5-9.4A4.1 4.1 0 0 1 12 8a4.1 4.1 0 0 1 7.5 2.6C19.5 15.4 12 20 12 20z" {...common} />}
      {kind === 'circle' && <circle cx="12" cy="12" r="8" {...common} />}
      {kind === 'moon' && <path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z" {...common} />}
      {kind === 'smile' && (
        <>
          <circle cx="12" cy="12" r="8.5" {...common} />
          <path d="M8.5 10.5h.01M15.5 10.5h.01" {...common} />
          <path d="M8.5 14.5s1.3 1.6 3.5 1.6 3.5-1.6 3.5-1.6" {...common} />
        </>
      )}
      {kind === 'flower' && (
        <>
          <circle cx="12" cy="12" r="2.4" {...common} />
          <path d="M12 9.6c0-3 1-4.6 0-6-1 1.4 0 3 0 6zM12 14.4c0 3 1 4.6 0 6-1-1.4 0-3 0-6z" {...common} />
          <path d="M9.6 12c-3 0-4.6-1-6 0 1.4 1 3 0 6 0zM14.4 12c3 0 4.6-1 6 0-1.4 1-3 0-6 0z" {...common} />
        </>
      )}
    </g>
  )
}

/**
 * 顶部手绘涂鸦背景。
 *
 * @param seed 用稳定 seed 选择图形组合，避免每次渲染抖动；
 *             传入当前时段（morning/night）可让氛围随时间变化。
 */
export function DoodleBackground({
  seed = 0,
  height = 168,
  className
}: {
  seed?: number
  height?: number
  className?: string
}): JSX.Element {
  // 用确定性伪随机，避免每次渲染都变（也避免 Math.random 造成 hydration 抖动）
  const rand = (i: number): number => {
    const v = Math.sin((seed + 1) * 9301 + i * 49297) * 233280
    return v - Math.floor(v)
  }

  const items = Array.from({ length: 14 }, (_, i) => ({
    kind: DOODLE_KINDS[Math.floor(rand(i) * DOODLE_KINDS.length) % DOODLE_KINDS.length],
    x: 10 + rand(i + 100) * 460,
    y: 8 + rand(i + 200) * (height - 30),
    size: 14 + rand(i + 300) * 26,
    rotate: -35 + rand(i + 400) * 70
  }))

  return (
    <svg
      className={['doodle-bg', className ?? ''].filter(Boolean).join(' ')}
      viewBox={`0 0 480 ${height}`}
      preserveAspectRatio="xMidYMid slice"
      aria-hidden="true"
      focusable="false"
    >
      {items.map((item, i) => (
        <DoodleShape key={i} {...item} />
      ))}
    </svg>
  )
}

/* -------------------------------------------------------------------------- */
/* 液态玻璃（FR-UI-3）                                                          */
/* -------------------------------------------------------------------------- */

/**
 * 液态玻璃容器。
 * 用 `backdrop-filter: blur()` + 渐变边框复刻 Android `LiquidGlass.kt`。
 */
export function LiquidGlass({
  children,
  className,
  strong = false,
  as: Tag = 'div'
}: {
  children: ReactNode
  className?: string
  strong?: boolean
  as?: 'div' | 'section' | 'header' | 'aside'
}): JSX.Element {
  return (
    <Tag className={['glass', strong ? 'glass-strong' : '', className ?? ''].filter(Boolean).join(' ')}>{children}</Tag>
  )
}

/* -------------------------------------------------------------------------- */
/* 主题切换揭示动画（FR-UI-4）                                                    */
/* -------------------------------------------------------------------------- */

/**
 * 主题切换的圆形扩散过渡。
 *
 * 复刻 Android `ThemeRevealContainer.kt`：以点击点为中心用 `clip-path: circle()`
 * 从 0 扩散到覆盖全屏。
 *
 * NFR-11 / `prefers-reduced-motion`：开启「减少动画」时直接切换，不播放过渡。
 */
export function ThemeReveal({
  active,
  origin,
  color,
  durationMs = 480
}: {
  active: boolean
  origin: { x: number; y: number }
  color: string
  durationMs?: number
}): JSX.Element | null {
  const [visible, setVisible] = useState(false)

  useEffect(() => {
    if (!active) {
      setVisible(false)
      return
    }
    setVisible(true)
    const timer = window.setTimeout(() => setVisible(false), durationMs)
    return () => window.clearTimeout(timer)
  }, [active, durationMs])

  if (!visible) return null

  const radius = Math.hypot(
    Math.max(origin.x, window.innerWidth - origin.x),
    Math.max(origin.y, window.innerHeight - origin.y)
  )

  return (
    <span
      className="theme-reveal"
      aria-hidden="true"
      style={{
        background: color,
        left: origin.x,
        top: origin.y,
        width: radius * 2,
        height: radius * 2,
        marginLeft: -radius,
        marginTop: -radius,
        animationDuration: `${durationMs}ms`
      }}
    />
  )
}

/** 是否应减少动画（NFR-11）。 */
export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false)
  useEffect(() => {
    const mq = window.matchMedia('(prefers-reduced-motion: reduce)')
    const handler = (): void => setReduced(mq.matches)
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }, [])
  return reduced
}
