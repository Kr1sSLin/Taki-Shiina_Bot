/**
 * 开机自启（FR-DSK-3、FR-SET-5、US-L4）。
 *
 * 写入 / 移除 `~/.config/autostart/tks-desktop.desktop`（XDG autostart 规范），
 * 支持「静默启动到托盘」（`--hidden`，FR-DSK-9）。
 */

import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { app } from 'electron'
import { createLogger } from '../app/logger'
import { paths } from '../app/paths'
import { ensureDirs } from '../app/paths'
import { iconPath } from './icons'

const log = createLogger('autostart')

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

export function isAutostartEnabled(): boolean {
  return existsSync(autostartFile())
}

/** 读取自启文件，判断是否带 `--hidden`（用于设置页回显）。 */
export function readAutostartHidden(): boolean {
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
