/**
 * Electron 主进程入口。
 *
 * 对标 Android 前台服务 `WebSocketService`：**连接与数据都在主进程**（FR-ARCH-1），
 * 窗口隐藏到托盘时 WS 不断开（G2 常驻可达）。
 *
 * 覆盖：FR-DSK-4（单实例）、FR-DSK-9（命令行参数）、NFR-5（安全基线 + CSP）、
 * NFR-13（崩溃恢复）、EDGE-L20（HiDPI）。
 */

import { app, dialog, session } from 'electron'
import { existsSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { resetSettings, updateSettings, getSettings } from './app/config-store'
import { closeLogger, createLogger, currentLogFile } from './app/logger'
import { appVersion, ensureDirs, paths } from './app/paths'
import { buildServices, type Services } from './app/services'
import { registerIpcHandlers } from './ipc/register'
import { normalizeAccelerator } from './desktop/shortcuts'
import { describeEnvironment } from './desktop/platform'
import { egressInfo } from './core/network/dispatcher'
import { registerAttachmentHandler, registerAttachmentScheme } from './desktop/protocol'

const log = createLogger('main')

/* -------------------------------------------------------------------------- */
/* 命令行参数（FR-DSK-9）                                                       */
/* -------------------------------------------------------------------------- */

interface CliArgs {
  hidden: boolean
  version: boolean
  resetConfig: boolean
  forceScaleFactor: number | null
  unknown: string[]
}

function parseArgs(argv: string[]): CliArgs {
  const out: CliArgs = { hidden: false, version: false, resetConfig: false, forceScaleFactor: null, unknown: [] }
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    if (arg === '--hidden') out.hidden = true
    else if (arg === '--version' || arg === '-v') out.version = true
    else if (arg === '--reset-config') out.resetConfig = true
    else if (arg === '--force-device-scale-factor') out.forceScaleFactor = Number(argv[++i]) || null
    else if (arg.startsWith('--force-device-scale-factor=')) {
      out.forceScaleFactor = Number(arg.split('=')[1]) || null
    } else if (arg.startsWith('--')) out.unknown.push(arg)
  }
  return out
}

const cli = parseArgs(process.argv.slice(1))

/* -------------------------------------------------------------------------- */
/* 启动期开关（必须在 app ready 之前）                                           */
/* -------------------------------------------------------------------------- */

/*
 * 沙箱模式**不在这里决定**——这段只负责把最终结果记进日志。
 *
 * Chromium 在主进程 JS 被求值**之前**就完成沙箱初始化，任一种不可用都是 FATAL 直接退出：
 *   - `chrome-sandbox` 存在但不是 root:4755 → setuid_sandbox_host.cc:
 *       "The SUID sandbox helper binary was found, but is not configured correctly."
 *     AppImage 从 nosuid 挂载点运行，永远满足不了；npm 装下来的也是 0755。
 *   - 加了 `--disable-setuid-sandbox` 但内核禁止非特权用户命名空间 → zygote_host_impl_linux.cc:
 *       "No usable sandbox!"（Ubuntu 24.04 起 apparmor_restrict_unprivileged_userns 默认为 1）
 *
 * 也就是说程序能跑到这一行，就说明沙箱已经初始化成功了，此时再
 * `app.commandLine.appendSwitch('disable-setuid-sandbox')` 既晚了也只会帮倒忙
 * （把已经生效的 SUID 沙箱往下降一级）。真正的选择发生在 exec 之前：
 *   - 打包版：build/sandbox-launcher.sh（由 scripts/after-pack.mjs 装成入口可执行文件）
 *   - 开发期：scripts/launch.mjs（`npm run dev` / `npm start`）
 */
function currentSandboxMode(): 'suid' | 'namespace' | 'none' {
  const argv = process.argv
  if (argv.includes('--no-sandbox')) return 'none'
  if (argv.includes('--disable-setuid-sandbox')) return 'namespace'
  return 'suid'
}

// EDGE-L20：HiDPI / 分数缩放。默认跟随 GDK_SCALE / Xft.dpi，可用参数强制覆盖。
if (cli.forceScaleFactor !== null) {
  app.commandLine.appendSwitch('force-device-scale-factor', String(cli.forceScaleFactor))
}

// 关闭 Chromium 的「后台节流」，保证托盘常驻期间 WS 心跳与定时器不被降频（G2）。
app.commandLine.appendSwitch('disable-background-timer-throttling')
app.commandLine.appendSwitch('disable-renderer-backgrounding')
app.commandLine.appendSwitch('disable-backgrounding-occluded-windows')

// Linux 上让通知与 WM_CLASS 正确归属（否则桌面通知可能显示为 "Electron"）。
app.setName('TKS Desktop')
if (process.platform === 'linux') {
  app.commandLine.appendSwitch('class', 'tks-desktop')
}

// FR-DSK-4：单实例锁——避免两个实例争抢同一 SQLite 与同一 WS 设备位
const gotLock = app.requestSingleInstanceLock()

// 受限附件协议必须在 app ready **之前**注册（Electron 要求）
registerAttachmentScheme()

if (!gotLock) {
  // 第二个实例：唤醒已有窗口后立即退出
  log.info('已存在运行中的实例，退出本次启动')
  app.quit()
} else {
  bootstrap()
}

function bootstrap(): void {
  let services: Services | null = null

  /* --------------------------------- 安全基线（NFR-5） ------------------- */

  function applySecurityPolicy(): void {
    // 只允许加载本地打包资源与已配置的后端域名；禁止 eval；禁止远程内容注入
    const settings = getSettings()
    const allowedConnect = new Set<string>(['self', 'file:'])
    try {
      const api = new URL(settings.apiBaseUrl)
      allowedConnect.add(`${api.protocol}//${api.host}`)
      allowedConnect.add(`${api.host}`)
    } catch {
      /* 配置非法时忽略，由设置页报错 */
    }
    try {
      const ws = new URL(settings.wsBaseUrl)
      allowedConnect.add(`${ws.protocol}//${ws.host}`)
      allowedConnect.add(`${ws.host}`)
    } catch {
      /* ignore */
    }
    // 开发期允许 Vite dev server 与 ws HMR
    const devUrl = process.env['ELECTRON_RENDERER_URL']
    if (devUrl) {
      try {
        const u = new URL(devUrl)
        allowedConnect.add(`${u.protocol}//${u.host}`)
        allowedConnect.add(`${u.protocol === 'https:' ? 'wss:' : 'ws:'}//${u.host}`)
      } catch {
        /* ignore */
      }
    }

    const csp = [
      "default-src 'self'",
      // Vite 在生产构建中会内联少量样式；开发期需要 unsafe-inline 支持 HMR
      devUrl ? "style-src 'self' 'unsafe-inline'" : "style-src 'self' 'unsafe-inline'",
      "img-src 'self' data: blob: tks-attachment:",
      "font-src 'self' data:",
      devUrl ? "script-src 'self' 'unsafe-inline' 'unsafe-eval'" : "script-src 'self'",
      "connect-src 'self' ws: wss: https: http:",
      "media-src 'self'",
      "object-src 'none'",
      "frame-src 'none'",
      "base-uri 'none'",
      "form-action 'none'"
    ].join('; ')

    void allowedConnect

    session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
      callback({
        responseHeaders: {
          ...details.responseHeaders,
          'Content-Security-Policy': [csp],
          'X-Content-Type-Options': ['nosniff']
        }
      })
    })

    // 拒绝一切权限请求（摄像头/麦克风/定位等本期均不需要）
    session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false))
  }

  /* ------------------------------------ 生命周期 ------------------------ */

  app.on('second-instance', () => {
    // FR-DSK-4：重复启动时唤起已有窗口而非新建进程
    log.info('检测到第二次启动，唤起已有窗口')
    services?.windows.show({ focusInput: true })
  })

  app.on('window-all-closed', () => {
    // 托盘常驻语义：窗口全关不代表退出（FR-ARCH-1 / G2）
    // 仅当托盘不可用时才跟随平台惯例退出（EDGE-L10）
    if (services && !services.tray.isAvailable()) {
      log.info('托盘不可用且所有窗口已关闭，退出应用')
      requestQuit()
    }
  })

  app.on('before-quit', () => {
    services?.windows.markQuitting()
  })

  app.on('will-quit', () => {
    try {
      services?.dispose()
    } catch (err) {
      log.error('释放服务失败', { error: String(err) })
    }
    closeLogger()
  })

  // NFR-13：未捕获异常不能静默消失
  process.on('uncaughtException', (err) => {
    log.error('未捕获异常', { error: err.message, stack: err.stack?.split('\n').slice(0, 4).join(' | ') })
  })
  process.on('unhandledRejection', (reason) => {
    log.error('未处理的 Promise 拒绝', { reason: String(reason) })
  })

  function requestQuit(): void {
    services?.windows.markQuitting()
    app.quit()
  }

  /* --------------------------------------- ready ------------------------ */

  void app.whenReady().then(() => {
    log.info('应用启动', {
      version: appVersion(),
      electron: process.versions.electron,
      argv: process.argv.slice(1),
      sandbox: currentSandboxMode(),
      // REST 出口（代理 / 直连）。WS 与 REST 走不同出口曾导致「能发不能同步」
      egress: egressInfo(),
      env: describeEnvironment()
    })

    if (currentSandboxMode() === 'none') {
      log.warn('当前以 --no-sandbox 运行：本机既没有可用的 SUID helper，内核也不允许非特权用户命名空间', {
        hint: '执行 scripts/fix-sandbox.sh 可恢复完整沙箱'
      })
    }

    if (cli.version) {
      // eslint-disable-next-line no-console
      console.log(appVersion())
      app.quit()
      return
    }

    if (cli.resetConfig) {
      log.warn('--reset-config：恢复默认配置')
      resetSettings()
    }

    const dirs = ensureDirs()
    log.info('目录已就绪', { ...dirs })

    applySecurityPolicy()
    registerAttachmentHandler()

    try {
      services = buildServices({ startHidden: cli.hidden, requestQuit })
    } catch (err) {
      log.error('服务初始化失败', { error: String(err) })
      // 数据库不可恢复时给出明确提示而非静默白屏
      dialog.showErrorBox('TKS Desktop', `启动失败：${String(err)}`)
      app.quit()
      return
    }

    const svc = services
    registerIpcHandlers(svc)

    // FR-DSK-9 / US-L4：`--hidden` 静默启动到托盘
    svc.windows.createMainWindow(cli.hidden)

    // EDGE-L10：GNOME 且托盘不可用时给出一次性引导
    const marker = join(paths().stateDir, '.gnome-tray-warned')
    svc.windows.maybeWarnGnomeTray(existsSync(marker), () => {
      try {
        writeFileSync(marker, new Date().toISOString())
      } catch {
        /* ignore */
      }
      // 同时把「关闭窗口行为」改为直接退出，避免用户关闭后无法唤起
      updateSettings({ closeBehavior: 'quit' })
    })

    log.info('启动完成', {
      logFile: currentLogFile(),
      integrity: svc.integrity,
      startHidden: cli.hidden,
      shortcut: normalizeAccelerator(getSettings().globalShortcut)
    })
  })
}
