/**
 * 组合根（Composition Root）：构造并连接所有主进程服务。
 *
 * 帧分发拓扑（§10.3）：
 *   ConnectionManager.frame
 *     ├─ chat.message.echo      → ChatService.handleEcho
 *     ├─ chat.queued            → ChatService.handleQueued
 *     ├─ chat.typing            → ChatService.handleTyping
 *     ├─ chat.reply.stream      → ChatService.handleReplyStream
 *     ├─ bot.error              → ChatService.handleBotError + 通知
 *     ├─ memory.fact.created    → ChatService.handleMemoryFact
 *     ├─ points.snapshot        → GamificationService.handlePointsSnapshot
 *     ├─ points.changed         → GamificationService.handlePointsChanged
 *     ├─ level.changed          → GamificationService.handleLevelChanged
 *     ├─ streak.warning         → GamificationService.handleStreakWarning
 *     └─ makeup_card.changed    → GamificationService.handleMakeupCardChanged
 */

import { net, powerMonitor } from 'electron'
import { IPC } from '@shared/ipc'
import type { ConnectionState, WsServerFrame } from '@shared/protocol'
import { bus } from './bus'
import { getSettings, updateSettings } from './config-store'
import { createLogger } from './logger'
import { ensureDirs, paths } from './paths'
import { t } from './i18n'
import { errorI18nKey } from '@shared/errors'
import { initDatabase, closeDatabase, type Db } from '../core/database/db'
import {
  deleteBlankBotMessages,
  normalizeStreamingMessages,
  pruneDelivered,
  pruneOrphanAttachments
} from '../core/database/repositories/message-repository'
import { listPendingReminders, pruneReminders } from '../core/database/repositories/reminder-repository'
import { AuthService, type AuthExpiredReason } from '../core/auth/auth-service'
import { clearTokens } from '../core/auth/token-manager'
import { RestClient } from '../core/network/rest-client'
import { ConnectionManager } from '../core/network/connection-manager'
import { ChatService } from '../core/chat/chat-service'
import { ImageService, initAttachmentDir } from '../core/chat/image-service'
import { NotificationService } from '../core/push/notification-service'
import { ReminderScheduler } from '../core/push/reminder-scheduler'
import { GamificationService } from '../core/gamification/gamification-service'
import { insertNotification, markNotificationsRead, unreadNotificationCount, markAllNotificationsRead } from '../core/database/repositories/notification-repository'
import { TrayManager } from '../desktop/tray'
import { ShortcutManager } from '../desktop/shortcuts'
import { WindowManager } from '../desktop/window-manager'
import { isAutostartEnabled, reconcileAutostart, setAutostart } from '../desktop/autolaunch'
import { trayLikelyAvailable } from '../desktop/platform'

const log = createLogger('services')

export interface Services {
  db: Db
  paths: ReturnType<typeof paths>
  auth: AuthService
  rest: RestClient
  conn: ConnectionManager
  chat: ChatService
  images: ImageService
  notify: NotificationService
  reminders: ReminderScheduler
  gamification: GamificationService
  tray: TrayManager
  shortcuts: ShortcutManager
  windows: WindowManager
  integrity: 'ok' | 'recovered'
  dispose(): void
}

export interface BuildOptions {
  /** FR-DSK-9：`--hidden` */
  startHidden: boolean
  /** 退出回调（托盘菜单 -> 真正退出）。 */
  requestQuit: () => void
}

export function buildServices(opts: BuildOptions): Services {
  /* ------------------------------- 目录与数据库 ------------------------- */

  const dirs = ensureDirs()
  initAttachmentDir()
  const { db, integrity } = initDatabase(dirs.databasePath)
  log.info('数据库就绪', { path: dirs.databasePath, integrity })

  // EDGE-L7：**真正**在启动流程中调用规整（Android 端的同名逻辑是死代码）
  const normalized = normalizeStreamingMessages()
  if (normalized.promoted || normalized.deleted) {
    log.info('EDGE-L7：已规整孤儿 streaming 消息', normalized)
  }
  // FR-CHAT-10：清理历史遗留空 Bot 消息
  const blanks = deleteBlankBotMessages()
  if (blanks) log.info('FR-CHAT-10：已清理空 Bot 消息', { count: blanks })
  // 兜底清理
  const orphans = pruneOrphanAttachments()
  if (orphans) log.info('已清理孤儿附件记录', { count: orphans })
  pruneDelivered()
  pruneReminders()

  /* --------------------------------- Windows ---------------------------- */

  // 托盘可用性先由平台启发式判断，创建成功后会被真实结果覆盖
  let trayOk = trayLikelyAvailable()

  // 通知服务在窗口管理器之后构造（窗口需要探测聚焦状态），这里用可变引用打破顺序依赖
  let notifyRef: NotificationService | null = null

  const windows = new WindowManager({
    trayAvailable: () => trayOk,
    onQuitRequested: () => opts.requestQuit(),
    // FR-NOTI-5：应用回到前台时清除所有消息类通知
    onWindowFocus: () => notifyRef?.dismissAll()
  })

  /* ------------------------------- 通知服务 ---------------------------- */

  const notify = new NotificationService({
    onActivate: ({ route, messageId }) => {
      windows.show({ navigate: route ? { to: route, params: messageId ? { messageId } : undefined } : undefined })
    },
    onReplyAction: () => {
      windows.show({ focusInput: true })
    }
  })
  notify.setWindowFocusedProbe(() => windows.focusedProbe())
  notifyRef = notify

  /* ------------------------------- 网络与鉴权 -------------------------- */

  // 先声明 rest，再由 authService 注入 provider（避免构造顺序上的循环依赖）
  let authService: AuthService | null = null
  const rest = new RestClient({
    auth: {
      getAccessToken: () => authService?.getAccessToken() ?? null,
      refreshAccessToken: () => authService?.refreshAccessToken() ?? Promise.resolve(null),
      onUnauthorized: (reason) => authService?.onUnauthorized(reason)
    },
    baseUrlProvider: () => getSettings().apiBaseUrl
  })

  const auth = new AuthService({
    rest,
    events: {
      onAuthenticated: () => {
        authService = auth
        bus.send(IPC.evtAuthChanged, { authenticated: true, userId: auth.describe().userId, reason: 'login' })
      },
      onAuthExpired: (reason: AuthExpiredReason) => {
        log.warn('凭据失效', { reason })
        conn.stop()
        chatRef?.handleConnectionLost()
        gamificationRef?.stopFallbackPolling()
        bus.send(IPC.evtAuthExpired, { reason, kicked: reason === 'kicked' })
        bus.send(IPC.evtAuthChanged, { authenticated: false, userId: null, reason: reason === 'kicked' ? 'kicked' : 'expired' })
        updateTray()
      },
      onLoggedOut: () => {
        conn.stop()
        chatRef?.handleConnectionLost()
        gamificationRef?.stopFallbackPolling()
        bus.send(IPC.evtAuthChanged, { authenticated: false, userId: null, reason: 'logout' })
        updateTray()
      }
    }
  })
  authService = auth

  /* ------------------------------- 提醒调度 ---------------------------- */

  const reminders = new ReminderScheduler({
    notify,
    events: {
      onFired: (record) => {
        log.info('提醒触发回调', { reminderId: record.reminderId })
      }
    }
  })

  /* ------------------------------- 连接管理 ---------------------------- */

  const conn = new ConnectionManager({ auth })

  /* ------------------------------- 聊天服务 ---------------------------- */

  let gamificationRef: GamificationService | null = null
  let chatRef: ChatService | null = null

  const chat = new ChatService({
    conn,
    rest,
    scheduleReminder: (input) => reminders.schedule(input),
    computeReminderFireAt: (target) => reminders.computeFireAt(target),
    insertBotNotification: (input) => {
      insertNotification(input)
      void refreshUnread()
    },
    /**
     * FR-NOTI-1 / FR-NOTI-2 / FR-NOTI-4：
     *   - `messageKind=greeting` → 走「问候」渠道，标题按 `greetingScenario` 区分早/晚
     *   - 其余（含互动回复）→ 走「聊天」渠道，互动回复加物品 emoji 前缀
     *   - 点击通知可唤起窗口并滚动定位到该消息（`messageId` → `evt:navigate`）
     */
    notifyReply: (info) => {
      const body = info.messages.map((m) => m.content).join('\n').trim()
      if (!body) return
      const messageId = info.messages[0]?.messageId

      if (info.messageKind === 'greeting') {
        const titleKey =
          info.greetingScenario === 'morning'
            ? 'notify.greeting.morning.title'
            : info.greetingScenario === 'night'
              ? 'notify.greeting.night.title'
              : 'notify.greeting.title'
        notify.notify({ kind: 'greeting', title: t(titleKey), body, route: '/chat', messageId })
        return
      }

      const prefix = info.interactionItemIcon
        ? `${info.interactionItemIcon}${info.interactionItemName ?? ''} `
        : ''
      notify.notify({ kind: 'chat', title: t('notify.chat.title'), body: `${prefix}${body}`, route: '/chat', messageId })
    },
    notifyError: (info) => {
      const detail = info.message || t(errorI18nKey(info.errorCode))
      notify.notify({ kind: 'error', title: t('notify.error.title'), body: detail, route: '/history' })
    },
    onAfterReply: () => {
      // FR-LV-6：用户当日完成一次对话后本地立即消除断签预警态
      gamificationRef?.clearStreakWarning()
      void gamificationRef?.balance().catch(() => undefined)
    },
    onAfterSync: () => {
      void gamificationRef?.refreshAll()
    }
  })
  chatRef = chat

  const images = new ImageService()

  /* --------------------------- 积分/等级/互动 --------------------------- */

  const gamification = new GamificationService({
    rest,
    notify,
    applyMergedRequestIds: (ids) => chat.applyMergedRequestIds(ids),
    isWsConnected: () => conn.isConnected(),
    navigate: (route) => bus.send(IPC.evtNavigate, { to: route })
  })
  gamificationRef = gamification

  /* ------------------------------- 帧分发 ------------------------------ */

  conn.on('frame', (frame: WsServerFrame) => {
    switch (frame.type) {
      case 'chat.message.echo':
        chat.handleEcho(frame)
        break
      case 'chat.queued':
        chat.handleQueued(frame)
        break
      case 'chat.typing':
        chat.handleTyping(frame)
        break
      case 'chat.reply.stream':
        chat.handleReplyStream(frame)
        break
      case 'bot.error':
        chat.handleBotError(frame)
        break
      case 'memory.fact.created':
        chat.handleMemoryFact(frame)
        break
      case 'points.snapshot':
        gamification.handlePointsSnapshot(frame.payload)
        break
      case 'points.changed':
        gamification.handlePointsChanged(frame.payload)
        break
      case 'level.changed':
        gamification.handleLevelChanged(frame.payload)
        break
      case 'streak.warning':
        gamification.handleStreakWarning(frame.payload)
        break
      case 'makeup_card.changed':
        gamification.handleMakeupCardChanged(frame.payload)
        break
      case 'pong':
        break
      default:
        // NFR-12：未知帧静默忽略
        break
    }
  })

  conn.on('state', (state: ConnectionState) => {
    bus.send(IPC.evtConnectionState, state)
    updateTray(state)
    bus.send(IPC.evtRestFallbackMode, { degraded: conn.isDegraded() })
  })

  conn.on('open', ({ fullSync }) => {
    log.info('连接就绪，开始同步', { fullSync })
    // FR-SYNC-4：首次登录 / 手动重连 / 上次全量失败 → `since=0`
    void chat.syncHistory(fullSync)
    // FR-PT-5：登录与 WS 重连成功后同步覆盖积分/等级缓存
    void gamification.refreshAll()
    gamification.setUserId(auth.describe().userId)
    gamification.stopFallbackPolling()
    updateTray()
  })

  conn.on('closed', ({ authFailure, code }) => {
    // FR-CONN-8：清理在途请求状态并标记 CONNECTION_LOST
    chat.handleConnectionLost()
    if (!authFailure) {
      // FR-PROG-6：轮询仅作为 WS 断开期间的兜底
      gamification.startFallbackPolling()
    }
    log.info('连接关闭处理完成', { authFailure, code })
  })

  conn.on('degraded', () => {
    bus.send(IPC.evtRestFallbackMode, { degraded: true })
  })

  /* --------------------------------- 托盘 ------------------------------ */

  const tray = new TrayManager({
    onToggleWindow: () => (windows.isVisible() && windows.isFocused() ? windows.hide() : windows.show()),
    onShowWindow: (focusInput) => windows.show({ focusInput }),
    onNavigate: (route) => windows.show({ navigate: { to: route } }),
    onReconnect: () => conn.reconnectManual(),
    onQuit: () => opts.requestQuit()
  })
  trayOk = tray.create()

  /* ------------------------------- 快捷键 ------------------------------ */

  const shortcuts = new ShortcutManager({
    onToggle: () => (windows.isVisible() && windows.isFocused() ? windows.hide() : windows.show({ focusInput: true }))
  })

  /* ------------------------------- 未读角标 ---------------------------- */

  async function refreshUnread(): Promise<number> {
    const count = unreadNotificationCount()
    tray.setUnread(count)
    bus.send(IPC.evtNotificationsUpdated, { unread: count })
    return count
  }

  function updateTray(state?: ConnectionState): void {
    tray.updateConnection(state ?? conn.getState())
  }

  /* ------------------------------- 电源事件 ---------------------------- */

  try {
    powerMonitor.on('resume', () => {
      // EDGE-L12：① 立即重连 ② 补发过期提醒 ③ 触发全量同步
      log.info('EDGE-L12：系统唤醒')
      reminders.revalidateOnResume()
      conn.reconnectNow('power-resume')
    })
  } catch (err) {
    log.warn('powerMonitor 绑定失败', { error: String(err) })
  }

  /* ------------------------------ 启动后置动作 ------------------------- */

  // FR-SET-5：把磁盘上的 autostart 状态同步回设置（用户可能手动删过 .desktop）
  const autostartState = reconcileAutostart()
  if (autostartState.onDisk !== getSettings().autostart) {
    log.info('自启状态与配置不一致，按磁盘为准', autostartState)
    updateSettings({ autostart: autostartState.onDisk, autostartHidden: autostartState.hidden })
  }

  reminders.start()

  if (auth.status().authenticated) {
    log.info('检测到有效凭据，自动建立连接')
    gamification.setUserId(auth.status().userId)
    conn.start()
  } else {
    log.info('无有效凭据，等待用户登录')
  }

  shortcuts.applyFromSettings()

  return {
    db,
    paths: dirs,
    auth,
    rest,
    conn,
    chat,
    images,
    notify,
    reminders,
    gamification,
    tray,
    shortcuts,
    windows,
    integrity,
    dispose(): void {
      log.info('正在释放服务…')
      shortcuts.dispose()
      tray.destroy()
      conn.dispose()
      gamification.stopFallbackPolling()
      reminders.dispose()
      closeDatabase()
    }
  }
}

/* -------------------------------------------------------------------------- */
/* 供 IPC 层复用的辅助                                                          */
/* -------------------------------------------------------------------------- */

export function currentNetworkOnline(): boolean {
  try {
    return net.isOnline()
  } catch {
    return true
  }
}

export function pendingReminders() {
  return listPendingReminders()
}

export function notificationsMarkRead(ids: string[]): void {
  markNotificationsRead(ids)
}

export function notificationsMarkAllRead(): void {
  markAllNotificationsRead()
}

export function notificationsUnread(): number {
  return unreadNotificationCount()
}

export function autostartEnabled(): boolean {
  return isAutostartEnabled()
}

export function applyAutostart(enabled: boolean, hidden: boolean): boolean {
  return setAutostart(enabled, hidden)
}

export function toast(level: 'info' | 'success' | 'warn' | 'error', i18nKey: string, params?: Record<string, string | number>): void {
  bus.send(IPC.evtToast, { level, i18nKey, params })
}

export function translateForMain(key: string, params?: Record<string, string | number>): string {
  return t(key, params)
}

export function clearAllCredentials(): void {
  clearTokens()
}
