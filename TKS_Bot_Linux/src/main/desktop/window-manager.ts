/**
 * 窗口管理（FR-ARCH-1、FR-UI-6/7、FR-NOTI-2/4/5、FR-SET-6、FR-DSK-8、EDGE-L9/L10）。
 *
 * 关键点：
 * - 隐藏到托盘时窗口只是 `hide()`，**主进程 WS 连接不受影响**（FR-ARCH-1 / NFR-2）。
 * - X11 下记忆窗口尺寸与位置；Wayland 下只记忆尺寸（EDGE-L9）。
 * - 托盘不可用时（GNOME 默认）自动把「关闭窗口行为」默认值改为「直接退出」（EDGE-L10）。
 */

import { BrowserWindow, app, nativeTheme, shell } from 'electron'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { IPC, type NavigateEvent } from '@shared/ipc'
import { bus } from '../app/bus'
import { getSettings, updateSettings } from '../app/config-store'
import { createLogger } from '../app/logger'
import { paths } from '../app/paths'
import { iconPath } from './icons'
import { canPersistWindowPosition, isAllowedExternalUrl, isWayland } from './platform'

const log = createLogger('window')

const MIN_WIDTH = 520
const MIN_HEIGHT = 640

/** 兜底显示窗口的等待上限：超时后无条件显示，宁可空白也不要「看不见的应用」。 */
const WINDOW_REVEAL_TIMEOUT_MS = 6000

/** 渲染进程崩溃后的自动重载次数上限，超过则改为展示诊断页（避免无限重载空转）。 */
const MAX_RENDERER_RELOADS = 3

/**
 * 定位打包产物内部的资源（预加载脚本、渲染页面）。
 *
 * ⚠️ **不要**直接写 `join(__dirname, '../preload/index.js')`。
 *
 * 主进程被 Rollup 代码分块后，被抽出的 chunk 里 `__dirname` 指向 chunk 所在目录
 * （默认是 `out/main/chunks`）而非入口目录，于是 `../preload`、`../renderer`
 * 会指向不存在的位置 —— 表现为**预加载加载失败 + 渲染页面 404 → 窗口一片空白**，
 * 且主进程侧几乎无提示。此问题在本项目真实发生过。
 *
 * 这里的做法是从 `__dirname` 起向上逐层探测，命中真实文件才返回；
 * 同时在找不到时**明确报错**而不是把错路径交给 Electron 静默失败。
 * 两条防线同时保留：`electron.vite.config.ts` 已把 chunk 与入口放同层，
 * 本函数则保证即使分块布局再变也不会静默白屏。
 */
function resolveBundleAsset(relative: string[], what: string): string | null {
  const target = relative.join('/')

  /*
   * 按「正常程度」顺序探测：
   *  ① `../<relative>` —— **标准布局**：入口与 chunk 同层（`out/main`），
   *     资源位于上一层的 `out/preload`、`out/renderer`。打包与开发期都成立。
   *  ② `./<relative>`  —— 万一资源被放到入口同级。
   *  ③ 逐层向上       —— chunk 落在 `out/main/chunks/` 之类子目录时的兜底；
   *                    命中时告警，提示打包布局已变化。
   */
  const candidates: Array<{ path: string; fallback: boolean }> = [
    { path: join(__dirname, '..', ...relative), fallback: false },
    { path: join(__dirname, ...relative), fallback: true }
  ]

  let dir = __dirname
  for (let depth = 0; depth < 3; depth += 1) {
    dir = join(dir, '..')
    candidates.push({ path: join(dir, ...relative), fallback: true })
  }

  for (const candidate of candidates) {
    if (!existsSync(candidate.path)) continue
    if (candidate.fallback) {
      log.warn('资源不在标准位置，已用兜底探测命中（打包布局可能已变化）', {
        what,
        resolved: candidate.path,
        dirname: __dirname
      })
    }
    return candidate.path
  }

  log.error('找不到打包资源，界面将无法加载', {
    what,
    target,
    dirname: __dirname,
    appPath: app.getAppPath(),
    tried: candidates.map((c) => c.path)
  })
  return null
}

export interface WindowManagerOptions {
  /** 托盘是否可用（EDGE-L10）。 */
  trayAvailable: () => boolean
  /** 关闭窗口时的行为回调；返回 true 表示已处理（隐藏），false 表示应退出。 */
  onQuitRequested: () => void
  /** FR-NOTI-5：窗口获得焦点（应用回到前台）时触发，用于清除消息类通知。 */
  onWindowFocus?: () => void
}

export class WindowManager {
  private mainWindow: BrowserWindow | null = null
  private secondary = new Set<BrowserWindow>()
  private opts: WindowManagerOptions
  private saveTimer: NodeJS.Timeout | null = null

  constructor(opts: WindowManagerOptions) {
    this.opts = opts
  }

  /* --------------------------------- 创建 --------------------------------- */

  /**
   * @param hidden FR-DSK-9：`--hidden` 静默启动到托盘
   */
  createMainWindow(hidden: boolean): BrowserWindow {
    if (this.mainWindow && !this.mainWindow.isDestroyed()) {
      if (!hidden) this.show()
      return this.mainWindow
    }

    const settings = getSettings()
    const { width, height, x, y } = settings.window
    const rememberPosition = canPersistWindowPosition()

    const win = new BrowserWindow({
      width: Math.max(MIN_WIDTH, width),
      height: Math.max(MIN_HEIGHT, height),
      x: rememberPosition && x !== null ? x : undefined,
      y: rememberPosition && y !== null ? y : undefined,
      minWidth: MIN_WIDTH,
      minHeight: MIN_HEIGHT,
      show: false,
      title: 'TKS Desktop',
      icon: iconPath(256),
      autoHideMenuBar: true,
      backgroundColor: nativeTheme.shouldUseDarkColors ? '#12141a' : '#f4f6fa',
      webPreferences: {
        // 见 resolveBundleAsset 的说明：不可用 __dirname 直接拼相对路径
        preload: resolveBundleAsset(['preload', 'index.js'], 'preload') ?? join(__dirname, '../preload/index.js'),
        // FR-ARCH-2 / NFR-5 安全基线
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        webSecurity: true,
        allowRunningInsecureContent: false,
        webviewTag: false,
        spellcheck: false
      }
    })

    this.mainWindow = win

    /**
     * 显示窗口。
     *
     * ⚠️ 这里刻意**不**只依赖 `ready-to-show`：
     *   `ready-to-show` 只在渲染进程完成首次绘制后触发，
     *   一旦渲染进程起不来（GPU/沙箱/资源缺失），该事件永不触发，
     *   而窗口是 `show: false` 创建的 —— 结果就是**进程在跑、托盘在、但永远没有画面**，
     *   且没有任何可见错误。因此补三条兜底路径（见下）。
     */
    const reveal = (reason: string): void => {
      if (revealed) return
      revealed = true
      if (hidden) {
        log.info('主窗口已就绪但按 --hidden 保持隐藏', { reason })
        return
      }
      if (win.isDestroyed()) return
      win.show()
      win.focus()
      log.info('主窗口已显示', { reason, visible: win.isVisible() })
    }
    let revealed = false

    win.once('ready-to-show', () => reveal('ready-to-show'))

    // 兜底 ②：DOM 加载完成也显示（某些环境 ready-to-show 会漏发）
    win.webContents.once('did-finish-load', () => reveal('did-finish-load'))

    // 兜底 ③：固定超时后无条件显示 —— 宁可出现一个空白窗口，也不要「看不见的应用」
    const revealTimer = setTimeout(() => {
      if (!revealed) {
        log.warn('未收到 ready-to-show / did-finish-load，按超时兜底显示窗口')
        reveal('timeout-fallback')
      }
    }, WINDOW_REVEAL_TIMEOUT_MS)
    revealTimer.unref?.()

    // 主框架加载失败：把失败原因直接呈现给用户，而不是留一片空白
    win.webContents.on('did-fail-load', (_event, errorCode, errorDescription, validatedURL, isMainFrame) => {
      if (!isMainFrame) return
      log.error('渲染页面加载失败', { errorCode, errorDescription, validatedURL })
      reveal('did-fail-load')
      this.showDiagnosticPage(win, {
        title: '界面加载失败',
        detail: `errorCode=${errorCode} ${errorDescription}`,
        url: validatedURL
      })
    })

    // EDGE-L9：Wayland 下不保存位置
    const persist = (): void => this.persistWindowState(win, rememberPosition)
    win.on('resize', () => this.debouncedPersist(persist))
    if (rememberPosition) win.on('move', () => this.debouncedPersist(persist))

    win.on('focus', () => {
      // FR-NOTI-5：应用回到前台时清除所有消息类通知
      this.opts.onWindowFocus?.()
    })

    win.on('close', (event) => {
      // FR-SET-6 / EDGE-L10
      const behavior = this.effectiveCloseBehavior()
      if (behavior === 'tray' && !this.isQuitting) {
        event.preventDefault()
        this.hide()
        log.debug('窗口已隐藏到托盘（连接保持）')
        return
      }
      this.persistWindowState(win, rememberPosition)
    })

    win.on('closed', () => {
      this.mainWindow = null
    })

    // FR-DSK-4 之外：渲染进程导航到外部地址一律交给系统浏览器，避免应用内被劫持
    win.webContents.setWindowOpenHandler(({ url }) => {
      if (isAllowedExternalUrl(url)) void shell.openExternal(url)
      return { action: 'deny' }
    })
    win.webContents.on('will-navigate', (event, url) => {
      const current = win.webContents.getURL()
      // 只允许 Vite dev server / file:// 自身的导航
      if (url !== current && !url.startsWith('file://') && !url.startsWith(process.env.ELECTRON_RENDERER_URL ?? '\u0000')) {
        event.preventDefault()
        if (isAllowedExternalUrl(url)) void shell.openExternal(url)
      }
    })

    this.loadRenderer(win)
    return win
  }

  /**
   * 把渲染层失败的原因**直接画在窗口里**。
   *
   * 之前的表现是：进程在跑、托盘在、日志里有 warn，但窗口一片空白（或干脆不显示），
   * 用户完全无从下手。现在至少能看到「哪里失败了、日志在哪」。
   *
   * 页面写到 state 目录再 `loadFile`，而不是用 `data:` URL ——
   * 后者在部分受限环境下同样会被拦。
   */
  private showDiagnosticPage(win: BrowserWindow, info: { title: string; detail: string; url?: string }): void {
    if (win.isDestroyed()) return
    try {
      const dir = paths().stateDir
      if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
      const file = join(dir, 'renderer-error.html')
      const esc = (s: string): string =>
        s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      const html = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>TKS Desktop — 界面加载失败</title>
<style>
  :root{color-scheme:dark light}
  body{margin:0;padding:32px;font:14px/1.7 system-ui,"Noto Sans CJK SC",sans-serif;background:#101319;color:#eef1f6}
  h1{font-size:18px;margin:0 0 12px}
  .box{border:1px solid #3a4152;border-radius:12px;padding:16px;background:#171b23;margin-bottom:16px}
  code,pre{font-family:ui-monospace,Menlo,monospace;font-size:12px}
  pre{white-space:pre-wrap;word-break:break-all;background:#0b0e13;padding:12px;border-radius:8px;border:1px solid #2a3040}
  .muted{color:#a6afbe}
  ol{padding-left:20px}
</style></head><body>
<h1>${esc(info.title)}</h1>
<div class="box"><pre>${esc(info.detail)}</pre>
${info.url ? `<p class="muted">目标地址：<code>${esc(info.url)}</code></p>` : ''}</div>
<div class="box">
  <p><strong>接下来可以这样做</strong></p>
  <ol>
    <li>查看日志目录：<code>${esc(paths().logsDir)}</code>（JSON 行，含 <code>scope</code> 与错误详情）</li>
    <li>若日志提示 GPU / 渲染进程崩溃，可用软件渲染启动：<code>tks-desktop --disable-gpu</code></li>
    <li>若提示预加载失败，请确认安装包完整（重装或校验 SHA256）</li>
    <li>仍无法解决时，把日志中的 <code>scope:"window"</code> 与 <code>scope:"main"</code> 行一并反馈</li>
  </ol>
  <p class="muted">此页面由主进程生成，用于替代原先的一片空白。</p>
</div>
</body></html>`
      writeFileSync(file, html, 'utf8')
      void win.loadFile(file)
      log.info('已加载渲染层诊断页', { file })
    } catch (err) {
      log.error('生成诊断页失败', { error: String(err) })
    }
  }

  /** 渲染进程崩溃自动重载，WS 连接不受影响（NFR-13）。 */
  private loadRenderer(win: BrowserWindow): void {
    let reloads = 0

    win.webContents.on('render-process-gone', (_event, details) => {
      log.error('渲染进程崩溃', { reason: details.reason, exitCode: details.exitCode, reloads })
      if (details.reason === 'clean-exit' || win.isDestroyed()) return

      if (reloads >= MAX_RENDERER_RELOADS) {
        // 不再无限重载空转：把原因摊开给用户看
        log.error('渲染进程反复崩溃，改为展示诊断页', { reloads })
        this.showDiagnosticPage(win, {
          title: '界面进程反复崩溃',
          detail: `reason=${details.reason} exitCode=${details.exitCode ?? 'n/a'}（已重试 ${reloads} 次）`
        })
        return
      }

      reloads += 1
      setTimeout(() => {
        if (!win.isDestroyed()) win.reload()
      }, 800)
    })

    /**
     * `preload-error`：预加载脚本自身抛错时 Electron 会发这个事件。
     *
     * 必须监听 —— 预加载失败会导致 `window.tks` 不存在、渲染端随即因
     * `window.tks.auth` 报错而白屏，而**主进程侧默认完全静默**，
     * 表现为「窗口在、但没有任何画面」，极难排查。
     */
    win.webContents.on('preload-error', (_event, preloadPath, error) => {
      log.error('预加载脚本执行失败（window.tks 将不存在，界面会白屏）', {
        preloadPath,
        error: error.message,
        stack: error.stack?.split('\n').slice(0, 3).join(' | ')
      })
      this.showDiagnosticPage(win, {
        title: '预加载脚本失败',
        detail: `${error.message}\n\n脚本路径：${preloadPath}`
      })
    })

    win.webContents.on('unresponsive', () => {
      log.warn('渲染进程无响应')
    })

    const devUrl = process.env['ELECTRON_RENDERER_URL']
    if (devUrl) {
      void win.loadURL(devUrl)
      return
    }
    const indexHtml = resolveBundleAsset(['renderer', 'index.html'], 'renderer/index.html')
    if (!indexHtml) {
      this.showDiagnosticPage(win, {
        title: '找不到渲染页面',
        detail: `未能在 ${__dirname} 及其上层目录中找到 renderer/index.html。\n安装可能不完整，请校验 SHA256 或重新安装。`
      })
      return
    }
    log.info('加载渲染页面', { indexHtml })
    void win.loadFile(indexHtml)
  }

  /* --------------------------------- 显隐 --------------------------------- */

  /** FR-DSK-2 toggle 语义 / FR-NOTI-4 点击通知唤起。 */
  show(options: { focusInput?: boolean; navigate?: NavigateEvent } = {}): void {
    const win = this.mainWindow ?? this.createMainWindow(false)
    if (win.isMinimized()) win.restore()
    if (!win.isVisible()) win.show()
    win.focus()

    if (options.navigate) bus.send(IPC.evtNavigate, options.navigate)
    if (options.focusInput) bus.send(IPC.evtFocusInput, null)
  }

  hide(): void {
    if (!this.mainWindow || this.mainWindow.isDestroyed()) return
    this.mainWindow.hide()
  }

  isVisible(): boolean {
    return !!this.mainWindow && !this.mainWindow.isDestroyed() && this.mainWindow.isVisible()
  }

  isFocused(): boolean {
    return !!this.mainWindow && !this.mainWindow.isDestroyed() && this.mainWindow.isFocused()
  }

  /** FR-NOTI-2：窗口前台且聚焦时抑制聊天类通知。 */
  focusedProbe(): boolean {
    return this.isFocused()
  }

  getMainWindow(): BrowserWindow | null {
    return this.mainWindow
  }

  /* --------------------------------- 多窗口 ------------------------------- */

  /** FR-DSK-8：独立弹出「记忆档案」「积分流水」窗口，便于与聊天窗并排查看。 */
  createSecondaryWindow(route: string, title: string): BrowserWindow {
    const win = new BrowserWindow({
      width: 720,
      height: 720,
      minWidth: 480,
      minHeight: 480,
      title,
      icon: iconPath(128),
      autoHideMenuBar: true,
      backgroundColor: nativeTheme.shouldUseDarkColors ? '#12141a' : '#f4f6fa',
      webPreferences: {
        preload: resolveBundleAsset(['preload', 'index.js'], 'preload') ?? join(__dirname, '../preload/index.js'),
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
        webSecurity: true,
        webviewTag: false
      }
    })
    this.secondary.add(win)
    win.on('closed', () => this.secondary.delete(win))

    const devUrl = process.env['ELECTRON_RENDERER_URL']
    if (devUrl) {
      void win.loadURL(`${devUrl}#${route}`)
    } else {
      const indexHtml = resolveBundleAsset(['renderer', 'index.html'], 'renderer/index.html')
      if (indexHtml) void win.loadFile(indexHtml, { hash: route })
    }
    win.once('ready-to-show', () => win.show())
    return win
  }

  /* ------------------------------ 窗口状态持久化 --------------------------- */

  private isQuitting = false

  markQuitting(): void {
    this.isQuitting = true
  }

  private debouncedPersist(fn: () => void): void {
    if (this.saveTimer) clearTimeout(this.saveTimer)
    this.saveTimer = setTimeout(fn, 500)
    this.saveTimer.unref?.()
  }

  private persistWindowState(win: BrowserWindow, rememberPosition: boolean): void {
    if (win.isDestroyed()) return
    try {
      const bounds = win.getBounds()
      updateSettings({
        window: {
          width: bounds.width,
          height: bounds.height,
          x: rememberPosition ? bounds.x : null,
          y: rememberPosition ? bounds.y : null
        }
      })
    } catch (err) {
      log.debug('窗口状态保存失败', { error: String(err) })
    }
  }

  /* ------------------------------ 关闭行为（EDGE-L10） -------------------- */

  /**
   * EDGE-L10：托盘不可用时自动把「关闭窗口行为」默认值改为「直接退出」，
   * 避免用户点了关闭后应用凭空消失且无法唤起。
   */
  effectiveCloseBehavior(): 'tray' | 'quit' {
    const settings = getSettings()
    if (!this.opts.trayAvailable()) {
      if (settings.closeBehavior === 'tray') {
        log.warn('EDGE-L10：托盘不可用，关闭窗口行为回退为「直接退出」')
        return 'quit'
      }
    }
    return settings.closeBehavior
  }

  /** EDGE-L10：首次检测到 GNOME 且托盘不可用时给出一次性引导。 */
  maybeWarnGnomeTray(markerFileExists: boolean, writeMarker: () => void): boolean {
    if (!this.opts.trayAvailable() && isWaylandOrGnome()) {
      if (!markerFileExists) {
        writeMarker()
        bus.send(IPC.evtToast, {
          level: 'warn',
          i18nKey: 'settings.closeBehavior.gnomeHint'
        })
        return true
      }
    }
    return false
  }

  hideOrQuit(): void {
    const behavior = this.effectiveCloseBehavior()
    if (behavior === 'tray') this.hide()
    else this.opts.onQuitRequested()
  }
}

function isWaylandOrGnome(): boolean {
  return isWayland() || (process.env.XDG_CURRENT_DESKTOP ?? '').toLowerCase().includes('gnome')
}
