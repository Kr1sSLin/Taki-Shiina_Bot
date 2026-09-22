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
 *
 * Windows（win32）没有 XDG，改用 Electron 惯用的 `app.getPath('userData')`
 * （即 `%APPDATA%\<应用名>`，本应用为 `app.setName('TKS Desktop')`，实测
 *  `C:\Users\<用户>\AppData\Roaming\TKS Desktop\`），并在其下派生三个目录：
 * - 配置：`%APPDATA%\TKS Desktop\`
 * - 数据：`%APPDATA%\TKS Desktop\data\`（SQLite 与图片附件）
 * - 日志：`%APPDATA%\TKS Desktop\state\logs\`
 * `TKS_*_DIR` 覆盖在两端都最优先；Linux / macOS 的路径与行为完全不变。
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

const IS_WINDOWS = process.platform === 'win32'

/**
 * Windows 下的根目录：Electron 惯用的 `app.getPath('userData')`（`%APPDATA%\<应用名>`）。
 *
 * `app.getPath()` 在主进程 require 阶段即可用（早于 `app.whenReady()`），本模块最早的调用点
 * 是 `registerAttachmentScheme()`（必须在 ready 之前注册协议）。为把风险压到最低，
 * 这里再兜一层：`app` 不可用 / `getPath` 抛错时退回 `%APPDATA%`，绝不因此影响启动顺序。
 */
function windowsUserDataDir(): string {
  try {
    return resolve(app.getPath('userData'))
  } catch {
    const appData = process.env.APPDATA?.trim()
    const base = appData ? resolve(appData) : join(homedir(), 'AppData', 'Roaming')
    return join(base, 'tks-desktop')
  }
}

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
  if (IS_WINDOWS) {
    // Windows：跟随 Electron 的 userData，在其下再分「配置 / 数据 / 状态」，
    // 与 Linux 的三分语义保持一致（TKS_*_DIR 覆盖仍然最优先）。
    const userData = windowsUserDataDir()
    const configDirWin = envDir('TKS_CONFIG_DIR') ?? userData
    const dataDirWin = envDir('TKS_DATA_DIR') ?? join(userData, 'data')
    const stateDirWin = envDir('TKS_STATE_DIR') ?? join(userData, 'state')

    return {
      configDir: configDirWin,
      dataDir: dataDirWin,
      stateDir: stateDirWin,
      logsDir: join(stateDirWin, 'logs'),
      attachmentsDir: join(dataDirWin, 'attachments'),
      databasePath: join(dataDirWin, 'tks.db'),
      settingsPath: join(configDirWin, 'settings.json'),
      credentialsPath: join(configDirWin, 'credentials.json'),
      // win32 的自启走注册表 Run 项（见 desktop/autolaunch.ts），不使用该文件路径；
      // 仅为接口兼容保留，并指向本应用自己的配置目录，避免误写到系统位置。
      autostartPath: join(configDirWin, 'autostart', 'tks-desktop.desktop')
    }
  }

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
