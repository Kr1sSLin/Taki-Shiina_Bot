/**
 * 全局快捷键与窗口内快捷键（FR-DSK-2、FR-DSK-6、FR-SET-7、EDGE-L8）。
 *
 * EDGE-L8：Electron `globalShortcut` 在 Wayland 下通常失效。
 * 方案：优先尝试 XDG Desktop Portal 的 `GlobalShortcuts` 接口（Electron 尚未暴露该 API，
 * 因此本模块做**真实能力探测**）；不可用时在设置页明确提示
 * 「当前会话为 Wayland，全局快捷键不可用，请改用托盘图标唤起」，并把该项开关置灰——
 * **而不是静默失效**。
 */

import { globalShortcut } from 'electron'
import type { ShortcutStatus } from '@shared/ipc'
import { PROTOCOL } from '@shared/levels'
import { createLogger } from '../app/logger'
import { getSettings, updateSettings } from '../app/config-store'
import { globalShortcutSupported, sessionType } from './platform'

const log = createLogger('shortcut')

export interface ShortcutCallbacks {
  /** FR-DSK-2：窗口隐藏时唤起并聚焦输入框；已聚焦时收回托盘（toggle 语义）。 */
  onToggle: () => void
}

/** 允许作为快捷键主体的键（Electron accelerator 语法子集）。 */
const MODIFIERS = ['CommandOrControl', 'Control', 'Ctrl', 'Alt', 'Shift', 'Super', 'Meta']

export class ShortcutManager {
  private callbacks: ShortcutCallbacks
  private registered: string | null = null

  constructor(callbacks: ShortcutCallbacks) {
    this.callbacks = callbacks
  }

  /** EDGE-L8：把「是否可用」明确暴露给设置页，而不是静默失败。 */
  status(): ShortcutStatus {
    const settings = getSettings()
    const available = globalShortcutSupported()
    return {
      enabled: settings.globalShortcutEnabled && available,
      available,
      sessionType: sessionType(),
      registered: this.registered,
      errorI18nKey: available ? null : 'settings.shortcut.waylandUnavailable'
    }
  }

  /**
   * 注册快捷键。
   * @returns 是否注册成功
   */
  register(accelerator: string): { ok: boolean; errorI18nKey: string | null } {
    const accel = normalizeAccelerator(accelerator)
    if (!accel) {
      return { ok: false, errorI18nKey: 'settings.shortcut.invalid' }
    }
    if (!globalShortcutSupported()) {
      log.warn('EDGE-L8：当前会话不支持全局快捷键', { sessionType: sessionType() })
      return { ok: false, errorI18nKey: 'settings.shortcut.waylandUnavailable' }
    }

    this.unregister()
    try {
      const ok = globalShortcut.register(accel, () => this.callbacks.onToggle())
      if (!ok) {
        log.warn('快捷键注册失败（可能已被占用）', { accelerator: accel })
        return { ok: false, errorI18nKey: 'settings.shortcut.conflict' }
      }
      this.registered = accel
      log.info('全局快捷键已注册', { accelerator: accel })
      return { ok: true, errorI18nKey: null }
    } catch (err) {
      log.error('快捷键注册异常', { accelerator: accel, error: String(err) })
      return { ok: false, errorI18nKey: 'settings.shortcut.invalid' }
    }
  }

  /** FR-SET-7：启动时按设置注册。 */
  applyFromSettings(): ShortcutStatus {
    const settings = getSettings()
    if (!settings.globalShortcutEnabled) {
      this.unregister()
      return this.status()
    }
    const result = this.register(settings.globalShortcut || PROTOCOL.DEFAULT_GLOBAL_SHORTCUT)
    if (!result.ok && result.errorI18nKey === 'settings.shortcut.conflict') {
      // 冲突时明确提示（FR-SET-7）
      log.warn('快捷键与系统或其他应用冲突，已保持未注册状态')
    }
    return this.status()
  }

  /** FR-SET-7：设置页修改（含冲突与非法格式时的明确提示）。 */
  update(accelerator: string, enabled: boolean): { status: ShortcutStatus; ok: boolean; errorI18nKey: string | null } {
    // ⚠️ 非法快捷键必须**报错**，不能静默替换成默认值。
    //    早先写法是 `normalizeAccelerator(x) ?? DEFAULT`：用户填了非法组合、
    //    界面显示「已保存」，实际生效的却是 Ctrl+Alt+T —— 既误导又难排查。
    const normalized = normalizeAccelerator(accelerator)
    if (!normalized) {
      log.warn('拒绝非法的全局快捷键', { accelerator })
      return { status: this.status(), ok: false, errorI18nKey: 'settings.shortcut.invalid' }
    }

    updateSettings({ globalShortcut: normalized, globalShortcutEnabled: enabled })

    if (!enabled) {
      this.unregister()
      return { status: this.status(), ok: true, errorI18nKey: null }
    }

    const result = this.register(normalized)
    return { status: this.status(), ok: result.ok, errorI18nKey: result.errorI18nKey }
  }

  unregister(): void {
    if (!this.registered) return
    try {
      globalShortcut.unregister(this.registered)
    } catch (err) {
      log.debug('注销快捷键失败', { error: String(err) })
    }
    log.debug('快捷键已注销', { accelerator: this.registered })
    this.registered = null
  }

  dispose(): void {
    try {
      globalShortcut.unregisterAll()
    } catch {
      /* ignore */
    }
    this.registered = null
  }
}

/**
 * 规范化 accelerator 字符串为 Electron 语法。
 * 接受 `Ctrl+Alt+T` / `control+alt+t` / `CommandOrControl+Shift+K` 等写法。
 */
export function normalizeAccelerator(input: string): string | null {
  const raw = (input ?? '').trim()
  if (!raw) return null

  const parts = raw.split('+').map((p) => p.trim()).filter(Boolean)
  if (parts.length < 2) {
    // 单键全局快捷键容易误触，拒绝注册并要求至少一个修饰键
    return null
  }

  const mapped: string[] = []
  let hasModifier = false
  let mainKey: string | null = null

  for (const part of parts) {
    const lower = part.toLowerCase()
    const modifier = MODIFIERS.find((m) => m.toLowerCase() === lower)
    if (modifier) {
      const canonical =
        modifier === 'Ctrl' ? 'Control' : modifier === 'Meta' ? 'Super' : modifier === 'CommandOrControl' ? 'CommandOrControl' : modifier
      if (!mapped.includes(canonical)) mapped.push(canonical)
      hasModifier = true
      continue
    }
    // 主键：单字符、F1–F24、方向键、常见具名键
    const upper = part.length === 1 ? part.toUpperCase() : capitalize(part)
    if (/^[A-Z0-9]$/.test(upper) || /^F([1-9]|1[0-9]|2[0-4])$/.test(upper) || NAMED_KEYS.has(upper)) {
      mainKey = upper
    } else {
      return null
    }
  }

  if (!hasModifier || !mainKey) return null
  return [...mapped, mainKey].join('+')
}

const NAMED_KEYS = new Set([
  'Space',
  'Tab',
  'Backspace',
  'Delete',
  'Insert',
  'Return',
  'Enter',
  'Escape',
  'Up',
  'Down',
  'Left',
  'Right',
  'Home',
  'End',
  'PageUp',
  'PageDown',
  'Plus',
  'Minus',
  'Comma',
  'Period',
  'Slash',
  'Backslash',
  'Semicolon',
  'Quote',
  'BracketLeft',
  'BracketRight',
  'Backquote'
])

function capitalize(value: string): string {
  if (value.length === 0) return value
  return value[0].toUpperCase() + value.slice(1).toLowerCase()
}
