/**
 * 图标资源解析（FR-UI-8）。
 *
 * 占位图由 `scripts/generate-icons.mjs` 程序化生成，完全符合 XDG Icon Theme
 * 多尺寸规范；正式美术资源到位后**直接替换同名 PNG** 即可，无需改代码。
 *
 * ⚠️ FR-UI-8：**不沿用** `Android_AI_Assistant/app/src/main/ic_launcher-playstore.png`
 * ——该文件只是应用商店上架素材，不是 Android 的实际图标资源，且不被任何代码引用。
 */

import { app } from 'electron'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { createLogger } from '../app/logger'

const log = createLogger('icons')

let baseDirCache: string | null = null

/**
 * 定位 `resources/icons` 目录。
 *
 * 需要同时覆盖四种运行形态（否则托盘与窗口图标会加载失败、托盘直接创建不出来）：
 *  ① 打包后（asar）：`<resources>/icons`（electron-builder 的 extraResources 放入）
 *  ② `electron .`（electron-vite dev / preview）：`app.getAppPath()/resources/icons`
 *  ③ `electron out/main/index.js`：此时 `app.getAppPath()` 会返回 `out/main`，
 *     必须再向上回溯两级才能到工程根
 *  ④ 从工程根以其他方式启动：`process.cwd()/resources/icons`
 *
 * 判定标准是**目录里真的有图标文件**，而不只是目录存在。
 */
function iconsDir(): string {
  if (baseDirCache) return baseDirCache

  const appPath = app.getAppPath()
  const candidates = [
    join(process.resourcesPath ?? '', 'icons'),
    join(appPath, 'resources', 'icons'),
    join(__dirname, '..', '..', 'resources', 'icons'),
    join(appPath, '..', '..', 'resources', 'icons'),
    join(process.cwd(), 'resources', 'icons')
  ].filter((dir) => dir.length > 0 && dir !== 'icons')

  const PROBE = 'tray-online.png'
  baseDirCache = candidates.find((dir) => existsSync(join(dir, PROBE))) ?? candidates[0]

  if (existsSync(join(baseDirCache, PROBE))) {
    log.debug('图标目录', { dir: baseDirCache })
  } else {
    log.warn('未找到图标资源目录，托盘与窗口图标将回退为系统默认', { tried: candidates })
  }
  return baseDirCache
}

function resolve(name: string): string {
  const full = join(iconsDir(), name)
  return existsSync(full) ? full : ''
}

/** 应用图标（供 BrowserWindow）；缺图时返回空串，Electron 会回退默认图标。 */
export function iconPath(size: 16 | 32 | 48 | 64 | 128 | 256 | 512 = 256): string {
  return resolve(`tks-${size}.png`) || resolve('icon.png')
}

/** 托盘图标（FR-CONN-9：以颜色区分在线/离线；未读加角标）。 */
export function trayIconPath(state: 'online' | 'offline' | 'unread'): string {
  const name = state === 'online' ? 'tray-online' : state === 'unread' ? 'tray-unread' : 'tray-offline'
  return resolve(`${name}.png`)
}

/** 等级徽章占位（FR-LV-2：最终配色由 `GET /level/config` + 熊猫图像.txt 驱动）。 */
export function levelBadgePath(levelCode: string, size: 64 | 128 = 128): string {
  const map: Record<string, string> = {
    PANDA_LV1: 'lv1-newborn',
    PANDA_LV2: 'lv2-curious',
    PANDA_LV3: 'lv3-rookie',
    PANDA_LV4: 'lv4-knight',
    PANDA_LV5: 'lv5-master',
    PANDA_LV6: 'lv6-elder',
    PANDA_LV7: 'lv7-legend'
  }
  const slug = map[levelCode]
  return slug ? resolve(`badge-${slug}-${size}.png`) : ''
}

/**
 * 互动物品占位图（FR-INT-9 / EDGE-L23）。
 * 客户端回退顺序：`iconUrl` 非空 → `icon`（emoji）→ 本占位图。
 */
export function itemPlaceholderPath(itemId: string, size: 64 | 128 = 128): string {
  return resolve(`item-${itemId}-${size}.png`)
}

/** 供打包配置与诊断使用。 */
export function iconsDirectory(): string {
  return iconsDir()
}
