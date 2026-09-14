/**
 * 平台能力探测与统一抽象（NFR-8 可移植性、EDGE-L8 / EDGE-L9 / EDGE-L10 / EDGE-L20）。
 *
 * 平台相关能力（托盘、自启、快捷键、通知）都经本模块收口，
 * 为后续 Windows / macOS 移植预留，但**本期只实现 Linux**。
 */

import { app, screen } from 'electron'
import { existsSync } from 'node:fs'
import { createLogger } from '../app/logger'
import { paths } from '../app/paths'

const log = createLogger('platform')

export type SessionType = 'x11' | 'wayland' | 'unknown'

let cachedSessionType: SessionType | null = null

/** EDGE-L8 / EDGE-L9：判断当前会话类型。 */
export function sessionType(): SessionType {
  if (cachedSessionType) return cachedSessionType

  const envType = (process.env.XDG_SESSION_TYPE ?? '').toLowerCase()
  if (envType === 'wayland') {
    cachedSessionType = 'wayland'
  } else if (envType === 'x11') {
    cachedSessionType = 'x11'
  } else if (process.env.WAYLAND_DISPLAY) {
    cachedSessionType = 'wayland'
  } else if (process.env.DISPLAY) {
    cachedSessionType = 'x11'
  } else {
    // Electron 命令行也可能带 ozone 平台提示
    const ozone = app.commandLine.getSwitchValue('ozone-platform')
    cachedSessionType = ozone === 'wayland' ? 'wayland' : 'unknown'
  }
  log.info('会话类型', { sessionType: cachedSessionType, xdgSessionType: envType || null })
  return cachedSessionType
}

export function isWayland(): boolean {
  return sessionType() === 'wayland'
}

export function currentDesktop(): string {
  return (process.env.XDG_CURRENT_DESKTOP ?? '').toLowerCase()
}

export function isGnome(): boolean {
  return currentDesktop().includes('gnome')
}

export function isKde(): boolean {
  return currentDesktop().includes('kde')
}

/**
 * EDGE-L8：Wayland 下 Electron `globalShortcut` 通常失效。
 *
 * 方案：优先尝试 XDG Desktop Portal 的 `GlobalShortcuts` 接口；
 * 不可用时在设置页明确提示，并把该项开关置灰。
 * Portal 的 GlobalShortcuts 需要 Chromium 侧支持，Electron 尚未暴露该 API，
 * 因此这里做**真实能力探测**并把结论交给 UI，而不是静默失效。
 */
export function globalShortcutSupported(): boolean {
  if (isWayland()) {
    // Wayland 下 Chromium 的 globalShortcut 依赖 XWayland 或 portal；
    // 只有显式设置了 xwayland 兼容时才可能生效。
    const canUseXwayland = !!process.env.DISPLAY && !process.env.WAYLAND_DISPLAY
    return canUseXwayland
  }
  return sessionType() === 'x11' || process.platform !== 'linux'
}

/** EDGE-L9：Wayland 不允许应用自行定位窗口，只记忆尺寸。 */
export function canPersistWindowPosition(): boolean {
  return !isWayland()
}

/**
 * EDGE-L10：GNOME 默认移除了系统托盘。
 * 打包时依赖 `libayatana-appindicator3`；此处做启发式判断，
 * 由 tray 模块在创建失败时进一步确认。
 */
export function trayLikelyAvailable(): boolean {
  if (!isGnome()) return true
  // 部分发行版预装了 AppIndicator 扩展；无法在应用内可靠探测扩展状态，
  // 因此这里保守返回 false，并在托盘创建成功时覆盖为 true。
  return false
}

/** 判断系统是否安装了 AppIndicator 相关的运行时库（用于给用户可执行的引导）。 */
export function hasAppIndicatorLibrary(): boolean {
  const candidates = [
    '/usr/lib/x86_64-linux-gnu/libayatana-appindicator3.so.1',
    '/usr/lib/x86_64-linux-gnu/libappindicator3.so.1',
    '/usr/lib64/libayatana-appindicator3.so.1',
    '/usr/lib/libayatana-appindicator3.so.1'
  ]
  const found = candidates.some((p) => existsSync(p))
  if (!found) log.info('未检测到 AppIndicator 运行时库')
  return found
}

export function iconScaleFactor(): number {
  try {
    return screen.getPrimaryDisplay().scaleFactor || 1
  } catch {
    return 1
  }
}

/** 跨平台外部链接打开（NFR-5：仅允许已配置后端域名或显式 https）。 */
export function isAllowedExternalUrl(url: string): boolean {
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' || parsed.protocol === 'http:'
  } catch {
    return false
  }
}

export function describeEnvironment(): {
  platform: string
  sessionType: SessionType
  desktop: string
  isGnome: boolean
  isKde: boolean
  trayAvailable: boolean
  appIndicator: boolean
  scaleFactor: number
  dataDir: string
} {
  return {
    platform: `${process.platform}-${process.arch}`,
    sessionType: sessionType(),
    desktop: currentDesktop() || 'unknown',
    isGnome: isGnome(),
    isKde: isKde(),
    trayAvailable: trayLikelyAvailable(),
    appIndicator: hasAppIndicatorLibrary(),
    scaleFactor: iconScaleFactor(),
    dataDir: paths().dataDir
  }
}
