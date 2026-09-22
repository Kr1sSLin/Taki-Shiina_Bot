/**
 * 开机自启（FR-DSK-3、FR-SET-5、US-L4）。
 *
 * 写入 / 移除 `~/.config/autostart/tks-desktop.desktop`（XDG autostart 规范），
 * 支持「静默启动到托盘」（`--hidden`，FR-DSK-9）。
 *
 * Windows（win32）没有 XDG autostart，改走注册表 Run 项：
 * `app.setLoginItemSettings()` / `app.getLoginItemSettings()`，对外语义完全相同
 * （开机自启开关 + `--hidden` 静默启动到托盘，FR-DSK-9）。
 */

import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { app } from 'electron'
import { createLogger } from '../app/logger'
import { paths } from '../app/paths'
import { ensureDirs } from '../app/paths'
import { iconPath } from './icons'

const log = createLogger('autostart')

const IS_WINDOWS = process.platform === 'win32'

const DESKTOP_TEMPLATE = (exec: string, icon: string): string =>
  [
    '[Desktop Entry]',
    'Type=Application',
    'Version=1.0',
    'Name=TKS Desktop',
    'Name[zh_CN]=TKS 桌面版',
    'Comment=Taki Shiina desktop client',
    'Comment[zh_CN]=椎名立希桌面客户端',
    `Exec=${exec}`,
    `Icon=${icon}`,
    'Terminal=false',
    'Categories=Network;InstantMessaging;Chat;',
    'StartupNotify=false',
    'X-GNOME-Autostart-enabled=true',
    'X-KDE-autostart-after=panel',
    ''
  ].join('\n')

function autostartFile(): string {
  return paths().autostartPath
}

/** 构造启动命令。开发期（未打包）用 `electron <appPath>`，打包后用可执行文件路径。 */
function buildExec(hidden: boolean): string {
  const suffix = hidden ? ' --hidden' : ''
  if (app.isPackaged) {
    // AppImage 里 `process.execPath` 指向本次运行的临时挂载点（/tmp/.mount_xxxxxx/tks-desktop），
    // 退出即消失，写进 autostart 下次开机必然启动失败。AppImage 运行时会把
    // .AppImage 自身的真实路径放在 $APPIMAGE，用它才稳定。
    const appImage = process.env.APPIMAGE
    if (appImage) return `"${appImage}"${suffix}`
    return `"${process.execPath}"${suffix}`
  }
  // 开发期：让 electron 直接跑工程目录
  return `"${process.execPath}" "${app.getAppPath()}"${suffix}`
}

function iconForDesktop(): string {
  // 打包安装后图标由 hicolor 主题提供，这里只需图标名；
  // 开发期指向工程内占位图标，避免出现空图标。
  // 复用 icons.ts 的探测逻辑：`app.getAppPath()` 在
  // `electron out/main/index.js` 形态下会返回 `out/main`，直接拼接会指错。
  if (app.isPackaged) return 'tks-desktop'
  const devIcon = iconPath(256)
  return devIcon || 'tks-desktop'
}

/* -------------------------------------------------------------------------- */
/* Windows：注册表 Run 项                                                      */
/* -------------------------------------------------------------------------- */

interface WinLoginItemSpec {
  path: string
  args: string[]
}

interface WinLoginItemState {
  openAtLogin: boolean
}

/**
 * Windows 启动项里登记的命令行（与 Linux `buildExec()` 一一对应）：
 * - 打包版：`<安装目录>\tks-desktop.exe [--hidden]`
 * - 开发期：`electron.exe "<工程目录>" [--hidden]`（`process.execPath` 是 electron.exe，
 *   不带上工程目录的话下次开机只能起一个空 Electron，与 Linux 开发期写法一致）
 */
function winLoginItem(hidden: boolean): WinLoginItemSpec {
  const args: string[] = []
  if (!app.isPackaged) args.push(`"${app.getAppPath()}"`)
  if (hidden) args.push('--hidden')
  return { path: process.execPath, args }
}

/**
 * 读回启动项状态。
 *
 * ⚠️ 必须与 `setLoginItemSettings` 传**完全相同**的 `path` / `args`。Electron 文档原文：
 *    "If you provided `path` and `args` options to `app.setLoginItemSettings`, then you
 *    need to pass the same arguments here for `openAtLogin` to be set correctly."
 *    （node_modules/electron/electron.d.ts:1166）
 *    因此 set / get 两侧统一走 `winLoginItem()`，避免「设了自启却读回 false」的自相矛盾。
 */
function winLoginItemState(hidden: boolean): WinLoginItemState {
  const spec = winLoginItem(hidden)
  return app.getLoginItemSettings({ path: spec.path, args: spec.args })
}

/**
 * 该可执行文件是否已登记启动项。
 * 文档：`executableWillLaunchAtLogin` 忽略 `args`，"will be true if the given executable
 * would be launched at login with **any** arguments"（electron.d.ts:1186），
 * 用它兜住不同 Electron 版本的 argv 比对差异。
 */
function winExecutableWillLaunchAtLogin(state: WinLoginItemState): boolean {
  return (state as { executableWillLaunchAtLogin?: boolean }).executableWillLaunchAtLogin === true
}

/** win32 的自启状态（对应 Linux 的「文件是否存在」+「内容是否含 --hidden」）。 */
function winAutostartState(): { enabled: boolean; hidden: boolean } {
  try {
    const plain = winLoginItemState(false)
    const hidden = winLoginItemState(true)
    return {
      enabled:
        plain.openAtLogin ||
        hidden.openAtLogin ||
        winExecutableWillLaunchAtLogin(plain) ||
        winExecutableWillLaunchAtLogin(hidden),
      // 只有「带 --hidden 的那条」被命中才算静默启动。
      // 若某个 Electron 版本在 win32 上忽略 argv 比对，这里会退化成「开了自启即为静默」，
      // 属可接受降级：设置页重新勾选一次即可纠正，不影响开关本身。
      hidden: hidden.openAtLogin
    }
  } catch (err) {
    // 读回失败按「未开启」处理（等价于 Linux 下文件不存在），不得向调用方抛错
    log.warn('读取 Windows 启动项失败', { error: String(err) })
    return { enabled: false, hidden: false }
  }
}

function setWindowsAutostart(enabled: boolean, hidden: boolean): boolean {
  try {
    if (!enabled) {
      // 关闭时把两种形态都清一遍：不同 Electron 版本改写注册表时的匹配键不完全一致，
      // 全清才不会留下「残留自启」
      for (const variant of [false, true]) {
        const spec = winLoginItem(variant)
        app.setLoginItemSettings({ openAtLogin: false, path: spec.path, args: spec.args })
      }
      log.info('已移除开机自启（Windows 启动项）')
      return true
    }

    const spec = winLoginItem(hidden)
    app.setLoginItemSettings({ openAtLogin: true, path: spec.path, args: spec.args })
    const applied = winAutostartState()
    log.info('已写入开机自启（Windows 启动项）', { path: spec.path, args: spec.args, hidden, applied })
    if (!applied.enabled) {
      // 读回失败 ≠ 写入失败（argv 比对行为随 Electron 版本不同）；返回值语义与 Linux
      // 分支保持一致：只表示「写入动作未抛错」。这里留一条 warn 便于事后定位。
      log.warn('启动项写入后未能读回，可能被系统策略拦截', { path: spec.path, args: spec.args })
    }
    return true
  } catch (err) {
    log.error('写入开机自启失败', { error: String(err), path: process.execPath })
    return false
  }
}

export function isAutostartEnabled(): boolean {
  if (IS_WINDOWS) return winAutostartState().enabled
  return existsSync(autostartFile())
}

/** 读取自启状态，判断是否带 `--hidden`（用于设置页回显）。 */
export function readAutostartHidden(): boolean {
  if (IS_WINDOWS) return winAutostartState().hidden
  try {
    const content = readFileSync(autostartFile(), 'utf8')
    return /--hidden\b/.test(content)
  } catch {
    return false
  }
}

/**
 * FR-SET-5：写入 / 移除 autostart 条目。
 * @returns 是否成功（失败时 UI 需给出明确提示）
 */
export function setAutostart(enabled: boolean, hidden: boolean): boolean {
  if (IS_WINDOWS) return setWindowsAutostart(enabled, hidden)

  const file = autostartFile()
  try {
    if (!enabled) {
      if (existsSync(file)) unlinkSync(file)
      log.info('已移除开机自启')
      return true
    }

    ensureDirs()
    const dir = dirname(file)
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })

    const content = DESKTOP_TEMPLATE(buildExec(hidden), iconForDesktop())
    writeFileSync(file, content, { mode: 0o644 })
    log.info('已写入开机自启', { file, hidden })
    return true
  } catch (err) {
    log.error('写入开机自启失败', { error: String(err), file })
    return false
  }
}

/** 同步设置与磁盘状态（处理用户手动删除 .desktop 的情况）。 */
export function reconcileAutostart(): { onDisk: boolean; hidden: boolean } {
  const onDisk = isAutostartEnabled()
  return { onDisk, hidden: onDisk ? readAutostartHidden() : false }
}
