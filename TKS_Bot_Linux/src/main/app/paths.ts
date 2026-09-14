/**
 * XDG 目录规范（FR-DSK-11 / FR-DSK-12）。
 *
 * - 配置：`~/.config/tks-desktop/`
 * - 数据：`~/.local/share/tks-desktop/`（SQLite 与图片附件）
 * - 日志：`~/.local/state/tks-desktop/logs/`
 *
 * 为支持便携 / 沙箱 / 测试运行，三者均可用环境变量覆盖：
 *   `TKS_CONFIG_DIR` / `TKS_DATA_DIR` / `TKS_STATE_DIR`
 * 叠加 Electron 的 `--user-data-dir` 语义时以本模块结果为唯一来源。
 */

import { app } from 'electron'
import { existsSync, mkdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { join, resolve } from 'node:path'

function envDir(name: string): string | null {
  const raw = process.env[name]
  if (!raw) return null
  const trimmed = raw.trim()
  return trimmed ? resolve(trimmed) : null
}

function xdgDir(envName: string, fallbackRelative: string): string {
  const raw = process.env[envName]
  const base = raw && raw.trim() ? raw.trim() : join(homedir(), fallbackRelative)
  return resolve(base)
}

let cached: Paths | null = null

export interface Paths {
  configDir: string
  dataDir: string
  stateDir: string
  logsDir: string
  attachmentsDir: string
  databasePath: string
  settingsPath: string
  credentialsPath: string
  autostartPath: string
}

function computePaths(): Paths {
  const configDir = envDir('TKS_CONFIG_DIR') ?? join(xdgDir('XDG_CONFIG_HOME', '.config'), 'tks-desktop')
  const dataDir = envDir('TKS_DATA_DIR') ?? join(xdgDir('XDG_DATA_HOME', '.local/share'), 'tks-desktop')
  const stateDir = envDir('TKS_STATE_DIR') ?? join(xdgDir('XDG_STATE_HOME', '.local/state'), 'tks-desktop')

  return {
    configDir,
    dataDir,
    stateDir,
    logsDir: join(stateDir, 'logs'),
    attachmentsDir: join(dataDir, 'attachments'),
    databasePath: join(dataDir, 'tks.db'),
    settingsPath: join(configDir, 'settings.json'),
    credentialsPath: join(configDir, 'credentials.json'),
    autostartPath: join(xdgDir('XDG_CONFIG_HOME', '.config'), 'autostart', 'tks-desktop.desktop')
  }
}

export function paths(): Paths {
  if (!cached) cached = computePaths()
  return cached
}

/** 确保所有目录存在（首次启动即建好，避免后续写失败）。 */
export function ensureDirs(): Paths {
  const p = paths()
  for (const dir of [p.configDir, p.dataDir, p.stateDir, p.logsDir, p.attachmentsDir]) {
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 })
  }
  return p
}

/** 日志中**禁止**出现 Token / 密码 / 图片 base64（NFR-6）。 */
export function redactForLog(value: unknown): unknown {
  if (typeof value === 'string') {
    if (value.length > 400) return `${value.slice(0, 64)}…(redacted ${value.length} chars)`
    return value
  }
  if (Array.isArray(value)) return value.map(redactForLog)
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      if (/token|password|secret|authorization|dataBase64|base64/i.test(k)) {
        out[k] = '[redacted]'
      } else {
        out[k] = redactForLog(v)
      }
    }
    return out
  }
  return value
}

export function appVersion(): string {
  // FR-SET-9：版本号必须从打包元数据读取，不得硬编码。
  try {
    return app.getVersion()
  } catch {
    return process.env.npm_package_version ?? '0.0.0'
  }
}
