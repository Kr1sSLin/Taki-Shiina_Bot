/**
 * Preload 沙箱桥接层（FR-ARCH-2）。
 *
 * `contextIsolation: true` + `sandbox: true`：渲染进程只能通过本文件暴露的
 * `window.tks` 白名单访问能力，**不能**直接触碰 `fs` / `net` / `child_process`。
 *
 * 本文件还负责把主进程的 `IpcResult` 信封还原为带 `code` / `i18nKey` 的错误对象，
 * 让渲染端能按服务端错误码查 i18n（§5.2 / §7.0）。
 */

import { contextBridge, ipcRenderer, type IpcRendererEvent } from 'electron'
import { IPC, type IpcResult, type TksApi } from '@shared/ipc'
import { encodeIpcErrorPayload, type IpcFailure } from '@shared/errors'

/** 还原后的错误：携带业务错误码与 i18n key。 */
export interface RendererError extends Error {
  code: number | null
  i18nKey: string
  appErrorCode: string | null
}

function makeError(failure: IpcFailure): RendererError {
  const i18nKey = failure.i18nKey || 'error.unknown'
  const code = failure.code ?? null
  const appErrorCode = failure.appErrorCode ?? null

  // ⚠️ `contextBridge` 传递 Error 时**只保留 message/stack/name**，自定义属性会被丢弃。
  //    因此必须把载荷编码进 message（详见 shared/errors.ts 的说明），
  //    否则 `err.code` / `err.i18nKey` 到渲染端会变成 undefined，
  //    导致 40201 / 40204 / 40206 等业务分支与错误码文案全部失效。
  const err = new Error(encodeIpcErrorPayload({ code, i18nKey, appErrorCode, message: failure.message })) as RendererError
  err.name = 'TksApiError'
  // 同上下文调用时仍可直接读这些属性
  err.code = code
  err.i18nKey = i18nKey
  err.appErrorCode = appErrorCode
  return err
}

async function invoke<T>(channel: string, ...args: unknown[]): Promise<T> {
  const result = (await ipcRenderer.invoke(channel, ...args)) as IpcResult<T> | T
  if (result && typeof result === 'object' && 'ok' in (result as object)) {
    const envelope = result as IpcResult<T>
    if (envelope.ok) return envelope.data
    throw makeError(envelope.error)
  }
  // 未包装的返回值（理论上不应出现，保留宽容处理）
  return result as T
}

/** 订阅主进程事件，返回取消订阅函数。 */
function on<T>(channel: string, cb: (payload: T) => void): () => void {
  const listener = (_event: IpcRendererEvent, payload: T): void => cb(payload)
  ipcRenderer.on(channel, listener)
  return () => {
    ipcRenderer.removeListener(channel, listener)
  }
}

const api: TksApi = {
  auth: {
    login: (username, password) => invoke(IPC.authLogin, username, password),
    logout: (clearLocalData) => invoke(IPC.authLogout, clearLocalData),
    status: () => invoke(IPC.authStatus),
    getDeviceId: () => invoke(IPC.authGetDeviceId),
    storageMode: () => invoke(IPC.authStorageMode),
    onChanged: (cb) => on(IPC.evtAuthChanged, cb),
    onExpired: (cb) => on(IPC.evtAuthExpired, cb)
  },
  connection: {
    getState: () => invoke(IPC.connGetState),
    reconnect: (manual) => invoke(IPC.connReconnect, manual),
    onState: (cb) => on(IPC.evtConnectionState, cb),
    onTyping: (cb) => on(IPC.evtTyping, cb),
    onQueued: (cb) => on(IPC.evtQueued, cb),
    onRestFallback: (cb) => on(IPC.evtRestFallbackMode, cb)
  },
  chat: {
    send: (input) => invoke(IPC.chatSend, input),
    sendViaRest: (input) => invoke(IPC.chatSendViaRest, input),
    listMessages: (opts) => invoke(IPC.chatListMessages, opts),
    loadOlder: (opts) => invoke(IPC.chatLoadOlder, opts),
    deleteMessage: (messageId) => invoke(IPC.chatDeleteMessage, messageId),
    clearConversation: () => invoke(IPC.chatClearConversation),
    retryMessage: (messageId) => invoke(IPC.chatRetryMessage, messageId),
    search: (keyword, limit) => invoke(IPC.chatSearch, keyword, limit),
    pendingState: () => invoke(IPC.chatPendingState),
    onMessagesUpdated: (cb) => on(IPC.evtMessagesUpdated, cb),
    onStreamDelta: (cb) => on(IPC.evtStreamDelta, cb),
    onStreamDone: (cb) => on(IPC.evtStreamDone, cb),
    onSyncStatus: (cb) => on(IPC.evtSyncStatus, cb)
  },
  images: {
    pick: () => invoke(IPC.imagePick),
    addPaths: (paths) => invoke(IPC.imageAddPaths, paths),
    readClipboard: () => invoke(IPC.imageReadClipboard),
    remove: (attachmentId) => invoke(IPC.imageRemove, attachmentId),
    listDraft: () => invoke(IPC.imageListDraft),
    clearDraft: () => invoke(IPC.imageClearDraft),
    saveAs: (localPath) => invoke(IPC.imageSaveAs, localPath),
    openExternal: (localPath) => invoke(IPC.imageOpenExternal, localPath),
    usage: () => invoke(IPC.imageAttachmentUsage),
    cleanup: (olderThanMs) => invoke(IPC.imageCleanup, olderThanMs),
    onChanged: (cb) => on(IPC.evtDraftImagesChanged, cb)
  },
  sync: {
    syncHistory: (full) => invoke(IPC.syncHistory, full)
  },
  history: {
    listNotifications: (limit) => invoke(IPC.historyListNotifications, limit),
    markRead: (ids) => invoke(IPC.historyMarkRead, ids),
    markAllRead: () => invoke(IPC.historyMarkAllRead),
    listFacts: (limit) => invoke(IPC.historyListFacts, limit),
    onNotificationsUpdated: (cb) => on(IPC.evtNotificationsUpdated, cb),
    onFactsUpdated: (cb) => on(IPC.evtFactsUpdated, cb)
  },
  reminders: {
    list: () => invoke(IPC.reminderList),
    cancel: (reminderId) => invoke(IPC.reminderCancel, reminderId),
    create: (input) => invoke(IPC.reminderCreate, input),
    onChanged: (cb) => on(IPC.evtRemindersChanged, cb)
  },
  settings: {
    get: () => invoke(IPC.settingsGet),
    update: (patch) => invoke(IPC.settingsUpdate, patch),
    testConnection: (apiBaseUrl) => invoke(IPC.settingsTestConnection, apiBaseUrl),
    getCity: () => invoke(IPC.settingsGetCity),
    setCity: (city) => invoke(IPC.settingsSetCity, city),
    setAutostart: (enabled, hidden) => invoke(IPC.settingsSetAutostart, enabled, hidden),
    setShortcut: (accelerator, enabled) => invoke(IPC.settingsSetShortcut, accelerator, enabled),
    shortcutStatus: () => invoke(IPC.settingsShortcutStatus),
    about: () => invoke(IPC.settingsAbout),
    export: (format) => invoke(IPC.settingsExport, format),
    openLogs: () => invoke(IPC.settingsOpenLogs),
    openDataDir: () => invoke(IPC.settingsOpenDataDir)
  },
  gamification: {
    overview: () => invoke(IPC.gamificationOverview),
    levelStatus: () => invoke(IPC.gamificationLevelStatus),
    levelConfig: () => invoke(IPC.gamificationLevelConfig),
    pointsHistory: (opts) => invoke(IPC.gamificationPointsHistory, opts),
    balance: () => invoke(IPC.gamificationBalance),
    interactionItems: () => invoke(IPC.gamificationInteractionItems),
    interactionSend: (itemId, requestId, text) => invoke(IPC.gamificationInteractionSend, itemId, requestId, text),
    makeupCard: () => invoke(IPC.gamificationMakeupCard),
    makeupCandidates: (limit) => invoke(IPC.gamificationMakeupCandidates, limit),
    makeupUse: (targetDate) => invoke(IPC.gamificationMakeupUse, targetDate),
    makeupHistory: (opts) => invoke(IPC.gamificationMakeupHistory, opts),
    cachedProgress: () => invoke(IPC.gamificationCachedProgress),
    onPointsChanged: (cb) => on(IPC.evtPointsChanged, cb),
    onLevelChanged: (cb) => on(IPC.evtLevelChanged, cb),
    onStreakWarning: (cb) => on(IPC.evtStreakWarning, cb),
    onMakeupCardChanged: (cb) => on(IPC.evtMakeupCardChanged, cb),
    onCelebration: (cb) => on(IPC.evtCelebration, cb)
  },
  desktop: {
    showWindow: () => invoke(IPC.desktopShowWindow),
    hideWindow: () => invoke(IPC.desktopHideWindow),
    setBadge: (count) => invoke(IPC.desktopSetBadge, count),
    setTheme: async (theme) => {
      await invoke(IPC.settingsUpdate, { theme })
    },
    getPlatform: () => invoke(IPC.desktopGetPlatform),
    openExternal: (url) => invoke(IPC.desktopOpenExternal, url),
    onSystemThemeChanged: (cb) => on(IPC.evtSystemThemeChanged, cb),
    onFocusInput: (cb) => {
      const unsub = on(IPC.evtFocusInput, () => cb())
      return unsub
    },
    onNavigate: (cb) => on(IPC.evtNavigate, cb),
    onToast: (cb) => on(IPC.evtToast, cb),
  }
}

contextBridge.exposeInMainWorld('tks', api)
