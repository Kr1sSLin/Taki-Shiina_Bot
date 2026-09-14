/**
 * 应用设置持久化（FR-CFG-1、FR-SET-1..9）。
 *
 * 原子写入（临时文件 + rename），避免断电/崩溃写坏配置。
 * 敏感信息（Token）**不在此处**，见 `core/auth/token-manager.ts`。
 */

import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import type { AppSettings, SettingsPatch } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { createLogger } from './logger'
import { ensureDirs, paths } from './paths'

const log = createLogger('config')

export const DEFAULT_SETTINGS: AppSettings = {
  apiBaseUrl: PROTOCOL.DEFAULT_API_BASE_URL,
  wsBaseUrl: PROTOCOL.DEFAULT_WS_BASE_URL,
  theme: 'system',
  city: '',
  autostart: false,
  autostartHidden: true,
  closeBehavior: 'tray',
  globalShortcut: PROTOCOL.DEFAULT_GLOBAL_SHORTCUT,
  globalShortcutEnabled: true,
  sendKey: 'enter',
  notifications: {
    chat: true,
    greeting: true,
    reminder: true,
    error: true,
    progress: true
  },
  doNotDisturb: {
    enabled: false,
    start: '23:00',
    end: '08:00'
  },
  window: {
    width: 900,
    height: 720,
    x: null,
    y: null
  },
  lastUsername: '',
  // EDGE-L11：safeStorage 不可用时需用户确认才降级为 0600 明文
  allowPlaintextCredentials: false
}

function settingsFile(): string {
  return paths().settingsPath
}

/** 深合并，仅接受已知键，忽略未知/类型不符的值。 */
function mergeSettings(base: AppSettings, patch: unknown): AppSettings {
  if (!patch || typeof patch !== 'object') return base
  const p = patch as Record<string, unknown>
  const out: AppSettings = {
    ...base,
    notifications: { ...base.notifications },
    doNotDisturb: { ...base.doNotDisturb },
    window: { ...base.window }
  }

  const str = (v: unknown, fallback: string): string => (typeof v === 'string' ? v : fallback)
  const num = (v: unknown, fallback: number): number => (typeof v === 'number' && Number.isFinite(v) ? v : fallback)
  const bool = (v: unknown, fallback: boolean): boolean => (typeof v === 'boolean' ? v : fallback)

  if ('apiBaseUrl' in p) out.apiBaseUrl = str(p.apiBaseUrl, base.apiBaseUrl)
  if ('wsBaseUrl' in p) out.wsBaseUrl = str(p.wsBaseUrl, base.wsBaseUrl)
  if ('theme' in p) {
    const t = str(p.theme, base.theme)
    out.theme = t === 'light' || t === 'dark' || t === 'system' ? t : base.theme
  }
  if ('city' in p) out.city = str(p.city, base.city)
  if ('autostart' in p) out.autostart = bool(p.autostart, base.autostart)
  if ('autostartHidden' in p) out.autostartHidden = bool(p.autostartHidden, base.autostartHidden)
  if ('closeBehavior' in p) {
    const c = str(p.closeBehavior, base.closeBehavior)
    out.closeBehavior = c === 'quit' ? 'quit' : 'tray'
  }
  if ('globalShortcut' in p) out.globalShortcut = str(p.globalShortcut, base.globalShortcut)
  if ('globalShortcutEnabled' in p) out.globalShortcutEnabled = bool(p.globalShortcutEnabled, base.globalShortcutEnabled)
  if ('sendKey' in p) {
    const s = str(p.sendKey, base.sendKey)
    out.sendKey = s === 'ctrl+enter' ? 'ctrl+enter' : 'enter'
  }
  if ('lastUsername' in p) out.lastUsername = str(p.lastUsername, base.lastUsername)
  if ('allowPlaintextCredentials' in p) {
    out.allowPlaintextCredentials = bool(p.allowPlaintextCredentials, base.allowPlaintextCredentials)
  }

  if (p.notifications && typeof p.notifications === 'object') {
    const n = p.notifications as Record<string, unknown>
    for (const key of ['chat', 'greeting', 'reminder', 'error', 'progress'] as const) {
      if (key in n) out.notifications[key] = bool(n[key], base.notifications[key])
    }
  }
  if (p.doNotDisturb && typeof p.doNotDisturb === 'object') {
    const d = p.doNotDisturb as Record<string, unknown>
    if ('enabled' in d) out.doNotDisturb.enabled = bool(d.enabled, base.doNotDisturb.enabled)
    if ('start' in d) out.doNotDisturb.start = str(d.start, base.doNotDisturb.start)
    if ('end' in d) out.doNotDisturb.end = str(d.end, base.doNotDisturb.end)
  }
  if (p.window && typeof p.window === 'object') {
    const w = p.window as Record<string, unknown>
    if ('width' in w) out.window.width = Math.max(520, num(w.width, base.window.width))
    if ('height' in w) out.window.height = Math.max(640, num(w.height, base.window.height))
    // EDGE-L9：Wayland 下不记忆位置
    if ('x' in w) out.window.x = typeof w.x === 'number' && Number.isFinite(w.x) ? w.x : null
    if ('y' in w) out.window.y = typeof w.y === 'number' && Number.isFinite(w.y) ? w.y : null
  }
  return out
}

let cache: AppSettings | null = null

export function getSettings(): AppSettings {
  if (cache) return cache
  const file = settingsFile()
  if (!existsSync(file)) {
    cache = { ...DEFAULT_SETTINGS }
    return cache
  }
  try {
    const raw = JSON.parse(readFileSync(file, 'utf8'))
    cache = mergeSettings(DEFAULT_SETTINGS, raw)
  } catch (err) {
    log.warn('配置文件解析失败，已回退默认值', { error: String(err) })
    cache = { ...DEFAULT_SETTINGS }
  }
  return cache
}

/** 原子写入：同目录临时文件 + rename。 */
export function saveSettings(next: AppSettings): void {
  ensureDirs()
  const file = settingsFile()
  const tmp = join(dirname(file), `.settings.${process.pid}.tmp`)
  writeFileSync(tmp, `${JSON.stringify(next, null, 2)}\n`, { mode: 0o600 })
  renameSync(tmp, file)
  cache = next
}

export function updateSettings(patch: SettingsPatch): AppSettings {
  const merged = mergeSettings(getSettings(), patch)
  saveSettings(merged)
  log.info('设置已更新', { keys: Object.keys(patch) })
  return merged
}

/** FR-DSK-9：`--reset-config` */
export function resetSettings(): AppSettings {
  cache = { ...DEFAULT_SETTINGS }
  saveSettings(cache)
  log.warn('设置已重置为默认值')
  return cache
}

/**
 * FR-CFG-2：仅允许 `https://` / `wss://`；`http://` / `ws://` 需二次确认，
 * 且仅对 `localhost` / `127.0.0.1` 免确认。
 */
export interface UrlPolicyResult {
  ok: boolean
  requiresConfirmation: boolean
  /** 供渲染端查 i18n 的原因键。 */
  reasonI18nKey: string | null
  normalized: string
}

export function validateApiBaseUrl(raw: string): UrlPolicyResult {
  const trimmed = (raw || '').trim()
  if (!trimmed) {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.empty', normalized: '' }
  }
  let url: URL
  try {
    url = new URL(trimmed)
  } catch {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.invalid', normalized: trimmed }
  }
  const scheme = url.protocol.replace(':', '').toLowerCase()
  if (scheme !== 'http' && scheme !== 'https') {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.scheme', normalized: trimmed }
  }
  const normalized = trimmed.endsWith('/') ? trimmed : `${trimmed}/`
  if (scheme === 'http') {
    const host = url.hostname.toLowerCase()
    const isLoopback = host === 'localhost' || host === '127.0.0.1' || host === '::1'
    return {
      ok: true,
      requiresConfirmation: !isLoopback,
      reasonI18nKey: isLoopback ? null : 'settings.url.warn.insecure',
      normalized
    }
  }
  return { ok: true, requiresConfirmation: false, reasonI18nKey: null, normalized }
}

export function validateWsBaseUrl(raw: string): UrlPolicyResult {
  const trimmed = (raw || '').trim()
  if (!trimmed) {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.empty', normalized: '' }
  }
  let url: URL
  try {
    url = new URL(trimmed)
  } catch {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.invalid', normalized: trimmed }
  }
  const scheme = url.protocol.replace(':', '').toLowerCase()
  if (scheme !== 'ws' && scheme !== 'wss') {
    return { ok: false, requiresConfirmation: false, reasonI18nKey: 'settings.url.error.wsScheme', normalized: trimmed }
  }
  const normalized = trimmed.replace(/\/+$/, '')
  if (scheme === 'ws') {
    const host = url.hostname.toLowerCase()
    const isLoopback = host === 'localhost' || host === '127.0.0.1' || host === '::1'
    return {
      ok: true,
      requiresConfirmation: !isLoopback,
      reasonI18nKey: isLoopback ? null : 'settings.url.warn.insecure',
      normalized
    }
  }
  return { ok: true, requiresConfirmation: false, reasonI18nKey: null, normalized }
}

/** 由 `apiBaseUrl` 推导默认 `wsBaseUrl`（同域名不同 scheme），便于设置页联动。 */
export function deriveWsBaseUrl(apiBaseUrl: string): string | null {
  try {
    const url = new URL(apiBaseUrl)
    if (url.protocol === 'https:') url.protocol = 'wss:'
    else if (url.protocol === 'http:') url.protocol = 'ws:'
    else return null
    url.pathname = ''
    url.search = ''
    url.hash = ''
    return url.toString().replace(/\/+$/, '')
  } catch {
    return null
  }
}
