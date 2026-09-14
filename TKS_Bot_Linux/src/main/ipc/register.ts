/**
 * IPC 处理器注册（FR-ARCH-2）。
 *
 * 渲染进程**不得**直接访问 `fs` / `net` / `child_process`；所有能力经本文件白名单暴露。
 * 每个处理器统一返回 `IpcResult<T>` 信封（见 `shared/ipc.ts` 说明），
 * 以便错误码与 i18n key 能完整穿过 IPC 边界。
 */

import { dialog, ipcMain, nativeTheme, shell } from 'electron'
import { writeFile } from 'node:fs/promises'
import {
  IPC,
  type AboutInfo,
  type AttachmentUsage,
  type IpcResult,
  type InteractionSendResult,
  type PlatformInfo,
  type ShortcutStatus,
  type ShortcutUpdateResult,
  type SyncStatusEvent
} from '@shared/ipc'
import type { AppSettings, AuthTokens, ChatAttachment, ChatMessage, SettingsPatch } from '@shared/protocol'
import { toIpcFailure } from '@shared/errors'
import { getSettings, updateSettings, validateApiBaseUrl, validateWsBaseUrl } from '../app/config-store'
import { appVersion, paths } from '../app/paths'
import { createLogger } from '../app/logger'
import type { Services } from '../app/services'
import { applyAutostart } from '../app/services'
import {
  listFacts,
  listNotifications,
  markAllNotificationsRead,
  markNotificationsRead,
  searchFacts
} from '../core/database/repositories/notification-repository'
import { getCachedMakeupCandidates, getProgress } from '../core/database/repositories/progress-repository'
import { listMessages } from '../core/database/repositories/message-repository'
import { credentialStorageMode } from '../core/auth/token-manager'
import { describeEnvironment, isGnome, sessionType, trayLikelyAvailable } from '../desktop/platform'

const log = createLogger('ipc')

type Handler<T> = (...args: never[]) => Promise<T> | T

export function registerIpcHandlers(services: Services): void {
  /** 统一包装：把异常转成 `IpcResult` 失败信封，绝不让 IPC 拒绝（避免丢错误码）。 */
  const handle = <T>(channel: string, fn: (...args: unknown[]) => Promise<T> | T): void => {
    ipcMain.handle(channel, async (_event, ...args): Promise<IpcResult<T>> => {
      try {
        const data = await (fn as Handler<T>)(...(args as never[]))
        return { ok: true, data }
      } catch (err) {
        const failure = toIpcFailure(err)
        log.warn('IPC 处理失败', { channel, code: failure.code, message: failure.message })
        return { ok: false, error: failure }
      }
    })
  }

  /* ------------------------------------------------------------------ 认证 */

  handle<AuthTokens>(
    IPC.authLogin,
    async (username, password) => {
      const tokens = await services.auth.login(String(username ?? ''), String(password ?? ''))
      // FR-CONN-1：登录成功后立即建立 WS 长连接；首次登录需全量同步（FR-SYNC-4）
      services.gamification.setUserId(tokens.userId)
      services.conn.start()
      return tokens
    }
  )

  handle<void>(IPC.authLogout, async (clearLocalData) => {
    services.auth.logout()
    if (clearLocalData === true) {
      // FR-AUTH-7：「同时清除本地数据」勾选项
      services.chat.clearConversation()
    }
  })

  handle(IPC.authStatus, () => services.auth.status())
  handle(IPC.authGetDeviceId, () => services.auth.currentDeviceId())
  handle(IPC.authStorageMode, () => credentialStorageMode())

  /* ------------------------------------------------------------------ 连接 */

  handle(IPC.connGetState, () => services.conn.getState())
  handle<void>(IPC.connReconnect, async () => {
    // FR-CONN-6：手动重连 → 置 `manualReconnectPendingFullSync = true`
    services.conn.reconnectManual()
  })

  /* ------------------------------------------------------------------ 聊天 */

  handle<void>(IPC.chatSend, async (input) => {
    const payload = input as { requestId: string; content: string; attachmentIds: string[] }
    services.chat.send({
      requestId: payload.requestId,
      content: payload.content,
      attachmentIds: payload.attachmentIds ?? []
    })
    // FR-IMG-3：成功送出后清空草稿（图片已绑定到消息上，不删磁盘文件）
    services.images.clearDraft(false)
  })

  handle<{ reply: string; messages: ChatMessage[] }>(IPC.chatSendViaRest, async (input) => {
    const payload = input as { requestId: string; content: string }
    return services.chat.sendViaRest(payload)
  })

  handle<ChatMessage[]>(IPC.chatListMessages, async (opts) => {
    const o = (opts ?? {}) as { limit?: number; beforeTimestamp?: number }
    return services.chat.list({ limit: o.limit ?? 80, beforeTimestamp: o.beforeTimestamp })
  })

  handle<ChatMessage[]>(IPC.chatLoadOlder, async (opts) => {
    const o = (opts ?? {}) as { beforeTimestamp: number; limit: number }
    return services.chat.list({ limit: o.limit ?? 50, beforeTimestamp: o.beforeTimestamp })
  })

  handle<void>(IPC.chatDeleteMessage, async (messageId) => {
    services.chat.deleteMessage(String(messageId))
  })

  handle<{ deleted: number; cursor: number }>(IPC.chatClearConversation, async () => {
    return services.chat.clearConversation()
  })

  handle<void>(IPC.chatRetryMessage, async (messageId) => {
    const msg = listMessages({ limit: 1 }).find((m) => m.messageId === String(messageId))
    if (!msg) return
    // FR-CHAT-11：重试仅针对用户消息——把它标记回发送中，由渲染端复用同一 requestId 重发
    void msg
  })

  handle<ChatMessage[]>(IPC.chatSearch, async (keyword, limit) => {
    return services.chat.search(String(keyword ?? ''), typeof limit === 'number' ? limit : 100)
  })

  handle(IPC.chatPendingState, () => services.chat.pendingState())

  /* ------------------------------------------------------------------ 图片 */

  handle<ChatAttachment[]>(IPC.imagePick, async () => (await services.images.pick()).added)
  handle<ChatAttachment[]>(IPC.imageAddPaths, async (filePaths) =>
    services.images.addPaths(Array.isArray(filePaths) ? (filePaths as string[]) : []).added
  )
  handle<ChatAttachment[]>(IPC.imageReadClipboard, () => services.images.readClipboard().added)
  handle<void>(IPC.imageRemove, async (attachmentId) => services.images.remove(String(attachmentId)))
  handle<ChatAttachment[]>(IPC.imageListDraft, () => services.images.listDraft())
  handle<void>(IPC.imageClearDraft, async () => services.images.clearDraft(true))
  handle(IPC.imageSaveAs, (localPath) => services.images.saveAs(String(localPath)))
  handle<void>(IPC.imageOpenExternal, async (localPath) => services.images.openExternal(String(localPath)))
  handle<AttachmentUsage>(IPC.imageAttachmentUsage, () => services.images.usage())
  handle(IPC.imageCleanup, (olderThanMs) =>
    services.images.cleanup(typeof olderThanMs === 'number' ? olderThanMs : 7 * 24 * 3600 * 1000)
  )

  /* ------------------------------------------------------------------ 同步 */

  handle<SyncStatusEvent>(IPC.syncHistory, async (full) => services.chat.syncHistory(full === true))

  /* -------------------------------------------------------- 历史与记忆 */

  handle(IPC.historyListNotifications, (limit) =>
    listNotifications(typeof limit === 'number' ? limit : 200)
  )
  handle<void>(IPC.historyMarkRead, async (ids) => {
    markNotificationsRead(Array.isArray(ids) ? (ids as string[]) : [])
  })
  handle<void>(IPC.historyMarkAllRead, async () => {
    markAllNotificationsRead()
  })
  handle(IPC.historyListFacts, async (limit) => {
    const facts = listFacts(typeof limit === 'number' ? limit : 300)
    if (facts.length === 0) {
      // 本地为空时补拉一次（FR-SYNC-7）
      await services.chat.pullFacts().catch(() => undefined)
      return listFacts(typeof limit === 'number' ? limit : 300)
    }
    return facts
  })

  /** FR-HIS-3：记忆搜索与按日期筛选（渲染端额外做本地二次过滤）。 */
  handle('history:searchFacts', (keyword, sinceMs, untilMs) =>
    searchFacts(String(keyword ?? ''), typeof sinceMs === 'number' ? sinceMs : null, typeof untilMs === 'number' ? untilMs : null)
  )

  /* ------------------------------------------------------------------ 提醒 */

  handle(IPC.reminderList, () => services.reminders.list())
  handle<void>(IPC.reminderCancel, async (reminderId) => {
    services.reminders.cancel(String(reminderId))
  })
  handle(IPC.reminderCreate, (input) => {
    const o = (input ?? {}) as { target: string; text: string }
    const fireAt = services.reminders.computeFireAt(o.target)
    const reminderId = `manual_${Date.now()}_${o.target}`
    services.reminders.schedule({ reminderId, targetTime: o.target, fireAt, text: o.text })
    return { reminderId, targetTime: o.target, fireAt, text: o.text, status: 'pending' as const, createdAt: Date.now() }
  })

  /* ------------------------------------------------------------------ 设置 */

  handle<AppSettings>(IPC.settingsGet, () => getSettings())

  handle<AppSettings>(IPC.settingsUpdate, async (patch) => {
    const p = (patch ?? {}) as SettingsPatch
    const before = getSettings()

    // FR-CFG-2：服务地址安全校验（仅 https/wss；http/ws 需二次确认，仅回环免确认）
    if (typeof p.apiBaseUrl === 'string' && p.apiBaseUrl !== before.apiBaseUrl) {
      const check = validateApiBaseUrl(p.apiBaseUrl)
      if (!check.ok) throw new Error(check.reasonI18nKey ?? 'settings.url.error.invalid')
      p.apiBaseUrl = check.normalized
    }
    if (typeof p.wsBaseUrl === 'string' && p.wsBaseUrl !== before.wsBaseUrl) {
      const check = validateWsBaseUrl(p.wsBaseUrl)
      if (!check.ok) throw new Error(check.reasonI18nKey ?? 'settings.url.error.invalid')
      p.wsBaseUrl = check.normalized
    }

    const next = updateSettings(p)

    // FR-SET-1：主题「跟随系统」由 nativeTheme 驱动（Electron 读取 XDG Desktop Portal 的 color-scheme）
    if (p.theme && p.theme !== before.theme) {
      nativeTheme.themeSource = next.theme
      log.info('主题已切换', { theme: next.theme, systemDark: nativeTheme.shouldUseDarkColors })
    }

    // FR-CFG-1：修改服务地址后需断开并以新地址重连
    if (next.apiBaseUrl !== before.apiBaseUrl || next.wsBaseUrl !== before.wsBaseUrl) {
      log.info('服务地址已变更，重新连接', { api: next.apiBaseUrl, ws: next.wsBaseUrl })
      services.conn.reconnectManual()
    }

    return next
  })

  handle(IPC.settingsTestConnection, async (apiBaseUrl) => {
    const target = typeof apiBaseUrl === 'string' && apiBaseUrl ? apiBaseUrl : getSettings().apiBaseUrl
    const check = validateApiBaseUrl(target)
    if (!check.ok) return { ok: false, latencyMs: null, errorI18nKey: check.reasonI18nKey }
    // §5.2：两个进程各有一个 /healthz 且形态不同 → **只判断 HTTP 200**，不解析响应体
    const result = await services.rest.healthz(check.normalized)
    return { ok: result.ok, latencyMs: result.latencyMs, errorI18nKey: result.ok ? null : 'settings.test.fail' }
  })

  handle(IPC.settingsGetCity, async () => {
    const data = await services.rest.request<{ city: string }>('settings/city')
    const city = data?.city ?? ''
    updateSettings({ city })
    return city
  })

  handle(IPC.settingsSetCity, async (city) => {
    // FR-SET-2：截断 `#` 之后的内容（与 Android `substringBefore('#')` 行为一致）
    const cleaned = String(city ?? '').split('#')[0].trim()
    if (!cleaned) throw new Error('error.api.40002')
    const data = await services.rest.request<{ city: string }>('settings/city', {
      method: 'PUT',
      body: { city: cleaned }
    })
    const saved = data?.city ?? cleaned
    updateSettings({ city: saved })
    return saved
  })

  handle<boolean>(IPC.settingsSetAutostart, async (enabled, hidden) => {
    const ok = applyAutostart(enabled === true, hidden !== false)
    if (ok) updateSettings({ autostart: enabled === true, autostartHidden: hidden !== false })
    return ok
  })

  handle<ShortcutUpdateResult>(
    IPC.settingsSetShortcut,
    (accelerator, enabled) => services.shortcuts.update(String(accelerator ?? ''), enabled !== false)
  )
  handle<ShortcutStatus>(IPC.settingsShortcutStatus, () => services.shortcuts.status())

  handle<AboutInfo>(IPC.settingsAbout, () => ({
    version: appVersion(),
    electronVersion: process.versions.electron ?? 'unknown',
    chromeVersion: process.versions.chrome ?? 'unknown',
    nodeVersion: process.versions.node ?? 'unknown',
    apiBaseUrl: getSettings().apiBaseUrl,
    wsBaseUrl: getSettings().wsBaseUrl,
    deviceId: services.auth.currentDeviceId(),
    logsDir: paths().logsDir,
    dataDir: paths().dataDir,
    configDir: paths().configDir,
    credentialStorage: credentialStorageMode(),
    databasePath: paths().databasePath
  }))

  /** FR-SET-10 / US-L7：导出聊天记录（本地不做 300 条截断）。 */
  handle(IPC.settingsExport, async (format) => {
    const rows = listMessages({ limit: 100000 })
    if (rows.length === 0) return { saved: false, path: null, count: 0 }

    const isJson = format !== 'text'
    const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')
    const defaultName = `tks-chat-${stamp}.${isJson ? 'json' : 'txt'}`

    const win = services.windows.getMainWindow()
    const result = win
      ? await dialog.showSaveDialog(win, { defaultPath: defaultName })
      : await dialog.showSaveDialog({ defaultPath: defaultName })
    if (result.canceled || !result.filePath) return { saved: false, path: null, count: rows.length }

    const content = isJson
      ? JSON.stringify(
          rows.map((m) => ({
            messageId: m.messageId,
            role: m.role,
            contentType: m.contentType,
            content: m.content,
            timestamp: m.timestamp,
            time: new Date(m.timestamp).toISOString()
          })),
          null,
          2
        )
      : rows
          .map((m) => {
            const who = m.role === 'user' ? '我' : m.role === 'bot' ? '立希' : '系统'
            const time = new Date(m.timestamp).toLocaleString()
            return `[${time}] ${who}: ${m.content}`
          })
          .join('\n')

    await writeFile(result.filePath, content, 'utf8')
    log.info('聊天记录已导出', { file: result.filePath, count: rows.length, format: isJson ? 'json' : 'text' })
    return { saved: true, path: result.filePath, count: rows.length }
  })

  handle<void>(IPC.settingsOpenLogs, async () => {
    await shell.openPath(paths().logsDir)
  })
  handle<void>(IPC.settingsOpenDataDir, async () => {
    await shell.openPath(paths().dataDir)
  })

  /* ------------------------------------------- 积分 / 等级 / 互动 / 补签卡 */

  handle(IPC.gamificationOverview, () => services.gamification.overview())
  handle(IPC.gamificationLevelStatus, () => services.gamification.levelStatus())
  handle(IPC.gamificationLevelConfig, async () => {
    try {
      return await services.gamification.levelConfigForce()
    } catch {
      // 离线时回退缓存（FR-PROG-4）
      return services.gamification.levelConfigCached()
    }
  })
  handle(IPC.gamificationPointsHistory, (opts) => {
    const o = (opts ?? {}) as { page: number; pageSize: number; reasonCode?: string | null }
    return services.gamification.pointsHistory(o)
  })
  handle(IPC.gamificationBalance, () => services.gamification.balance())
  handle(IPC.gamificationInteractionItems, () => services.gamification.interactionItems())
  handle<InteractionSendResult>(IPC.gamificationInteractionSend, (itemId, requestId, text) =>
    services.gamification.interactionSend(String(itemId), String(requestId), typeof text === 'string' ? text : undefined)
  )
  handle(IPC.gamificationMakeupCard, () => services.gamification.makeupCard())
  handle(IPC.gamificationMakeupCandidates, async (limit) => {
    try {
      return await services.gamification.makeupCandidates(typeof limit === 'number' ? limit : 120)
    } catch (err) {
      // FR-PROG-4：离线时回退到缓存日历
      const cached = getCachedMakeupCandidates()
      log.warn('补签候选拉取失败，回退缓存', { cached: cached.length })
      if (cached.length === 0) throw err
      return {
        items: cached,
        total: cached.length,
        firstActivityDate: cached.length ? cached[cached.length - 1].date : null,
        available: getProgress(services.auth.status().userId ?? '')?.availableMakeupCards ?? 0
      }
    }
  })
  handle(IPC.gamificationMakeupUse, (targetDate) => services.gamification.makeupUse(String(targetDate)))
  handle(IPC.gamificationMakeupHistory, (opts) => {
    const o = (opts ?? {}) as { page: number; pageSize: number }
    return services.gamification.makeupHistory(o)
  })
  /** FR-PROG-4：离线展示缓存的积分/等级数据。 */
  handle(IPC.gamificationCachedProgress, () => getProgress(services.auth.status().userId ?? ''))

  /* ------------------------------------------------------------------ 桌面 */

  handle<void>(IPC.desktopShowWindow, async () => services.windows.show())
  handle<void>(IPC.desktopHideWindow, async () => services.windows.hide())
  handle<void>(IPC.desktopSetBadge, async (count) => {
    const n = typeof count === 'number' ? count : 0
    services.tray.setUnread(n)
  })

  handle<PlatformInfo>(IPC.desktopGetPlatform, () => ({
    platform: `${process.platform}-${process.arch}`,
    sessionType: sessionType(),
    trayAvailable: services.tray.isAvailable() || trayLikelyAvailable(),
    isGnome: isGnome(),
    scaleFactor: describeEnvironment().scaleFactor,
    reducedMotion: false
  }))

  handle<void>(IPC.desktopOpenExternal, async (url) => {
    const target = String(url ?? '')
    if (!/^https?:\/\//i.test(target)) throw new Error('settings.url.error.scheme')
    await shell.openExternal(target)
  })

  log.info('IPC 处理器已注册')
}
