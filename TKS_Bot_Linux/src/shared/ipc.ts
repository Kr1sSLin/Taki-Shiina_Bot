/**
 * IPC 契约（FR-ARCH-2）：渲染进程只能通过 preload 白名单 IPC 访问能力，
 * 不得直接触碰 `fs` / `net` / `child_process`。
 *
 * 本文件同时定义「通道名」与「主进程 → 渲染进程事件」，主/渲染共用以保证类型一致。
 */

import type {
  AppSettings,
  AuthTokens,
  BotNotification,
  ChatAttachment,
  ChatMessage,
  ConnectionState,
  InteractionItemsData,
  InteractionSendData,
  LevelConfigData,
  LevelStatus,
  MakeupCardHistoryData,
  MakeupCardSummary,
  MakeupCandidatesData,
  MakeupCardUseData,
  PointsHistoryData,
  PointsLedgerEntry,
  PointsOverviewData,
  ReminderRecord,
  SettingsPatch,
  UserFact,
  UserProgressCache
} from './protocol'
import type { IpcFailure } from './errors'

/**
 * IPC 统一结果信封。
 *
 * Electron 在 `ipcRenderer.invoke` 拒绝时会把自定义 Error 的附加字段（`code` / `i18nKey`）
 * 抹掉，只留下拼接后的 message。为了让渲染端能按错误码查 i18n（§5.2 / §7.0 的错误码体系），
 * 主进程统一**不抛错**，而是返回本信封；preload 再还原为带 `code` 的错误对象。
 */
export type IpcResult<T> = { ok: true; data: T } | { ok: false; error: IpcFailure }

/* -------------------------------------------------------------------------- */
/* 通道名                                                                       */
/* -------------------------------------------------------------------------- */

export const IPC = {
  // 认证
  authLogin: 'auth:login',
  authLogout: 'auth:logout',
  authStatus: 'auth:status',
  authGetDeviceId: 'auth:getDeviceId',
  authStorageMode: 'auth:storageMode',

  // 连接
  connGetState: 'conn:getState',
  connReconnect: 'conn:reconnect',

  // 聊天
  chatSend: 'chat:send',
  chatListMessages: 'chat:listMessages',
  chatLoadOlder: 'chat:loadOlder',
  chatDeleteMessage: 'chat:deleteMessage',
  chatClearConversation: 'chat:clearConversation',
  chatRetryMessage: 'chat:retryMessage',
  chatSearch: 'chat:search',
  chatSendViaRest: 'chat:sendViaRest',
  chatPendingState: 'chat:pendingState',

  // 图片
  imagePick: 'image:pick',
  imageAddPaths: 'image:addPaths',
  imageReadClipboard: 'image:readClipboard',
  imageRemove: 'image:remove',
  imageListDraft: 'image:listDraft',
  imageClearDraft: 'image:clearDraft',
  imageSaveAs: 'image:saveAs',
  imageOpenExternal: 'image:openExternal',
  imageAttachmentUsage: 'image:attachmentUsage',
  imageCleanup: 'image:cleanup',

  // 同步
  syncHistory: 'sync:syncHistory',

  // 历史与记忆
  historyListNotifications: 'history:listNotifications',
  historyMarkRead: 'history:markRead',
  historyMarkAllRead: 'history:markAllRead',
  historyListFacts: 'history:listFacts',

  // 提醒
  reminderList: 'reminder:list',
  reminderCancel: 'reminder:cancel',
  reminderCreate: 'reminder:create',

  // 设置
  settingsGet: 'settings:get',
  settingsUpdate: 'settings:update',
  settingsTestConnection: 'settings:testConnection',
  settingsGetCity: 'settings:getCity',
  settingsSetCity: 'settings:setCity',
  settingsSetAutostart: 'settings:setAutostart',
  settingsSetShortcut: 'settings:setShortcut',
  settingsShortcutStatus: 'settings:shortcutStatus',
  settingsAbout: 'settings:about',
  settingsExport: 'settings:export',
  settingsOpenLogs: 'settings:openLogs',
  settingsOpenDataDir: 'settings:openDataDir',

  // 积分 / 等级 / 互动
  gamificationOverview: 'gamification:overview',
  gamificationLevelStatus: 'gamification:levelStatus',
  gamificationLevelConfig: 'gamification:levelConfig',
  gamificationPointsHistory: 'gamification:pointsHistory',
  gamificationBalance: 'gamification:balance',
  gamificationInteractionItems: 'gamification:interactionItems',
  gamificationInteractionSend: 'gamification:interactionSend',
  gamificationMakeupCard: 'gamification:makeupCard',
  gamificationMakeupCandidates: 'gamification:makeupCandidates',
  gamificationMakeupUse: 'gamification:makeupUse',
  gamificationMakeupHistory: 'gamification:makeupHistory',
  gamificationCachedProgress: 'gamification:cachedProgress',

  // 桌面
  desktopShowWindow: 'desktop:showWindow',
  desktopHideWindow: 'desktop:hideWindow',
  desktopSetBadge: 'desktop:setBadge',
  desktopGetPlatform: 'desktop:getPlatform',
  desktopOpenExternal: 'desktop:openExternal',

  // 主进程 → 渲染进程（单向推送）
  evtConnectionState: 'evt:connectionState',
  evtMessagesUpdated: 'evt:messagesUpdated',
  evtStreamDelta: 'evt:streamDelta',
  evtStreamDone: 'evt:streamDone',
  evtTyping: 'evt:typing',
  evtQueued: 'evt:queued',
  evtAuthExpired: 'evt:authExpired',
  evtAuthChanged: 'evt:authChanged',
  evtNotificationCreated: 'evt:notificationCreated',
  evtNotificationsUpdated: 'evt:notificationsUpdated',
  evtFactsUpdated: 'evt:factsUpdated',
  evtPointsChanged: 'evt:pointsChanged',
  evtLevelChanged: 'evt:levelChanged',
  evtStreakWarning: 'evt:streakWarning',
  evtMakeupCardChanged: 'evt:makeupCardChanged',
  evtPointsSnapshot: 'evt:pointsSnapshot',
  evtCelebration: 'evt:celebration',
  evtToast: 'evt:toast',
  evtNavigate: 'evt:navigate',
  evtDraftImagesChanged: 'evt:draftImagesChanged',
  evtRemindersChanged: 'evt:remindersChanged',
  evtSystemThemeChanged: 'evt:systemThemeChanged',
  evtFocusInput: 'evt:focusInput',
  evtSyncStatus: 'evt:syncStatus',
  evtRestFallbackMode: 'evt:restFallbackMode'
} as const

/* -------------------------------------------------------------------------- */
/* 事件负载                                                                     */
/* -------------------------------------------------------------------------- */

/** 流式增量（FR-CHAT-5）：首个非空 delta 时渲染端创建 `pending_{requestId}` 占位。 */
export interface StreamDeltaEvent {
  requestId: string
  delta: string
  contentType: 'text' | 'image' | 'mixed'
  modelProvider: string
}

/** 一条最终 Bot 消息（已按 `\n` 拆分后的单条气泡，FR-CHAT-6）。 */
export interface StreamDoneEvent {
  requestId: string
  message: ChatMessage
  /** 该回复对应的全部拆分行（含本条），便于一次性插入。 */
  allMessages: ChatMessage[]
  requestIds: string[]
  timerInstruction: { target: string; text: string } | null
  messageKind: string | null
  interactionItemIcon: string | null
  interactionItemName: string | null
  interactionFailed: boolean
}

export interface TypingEvent {
  typing: boolean
  stage?: 'vision' | 'generating' | 'interaction' | 'interaction_merge'
}

export interface SyncStatusEvent {
  ok: boolean
  mode: 'incremental' | 'full'
  inserted: number
  /** 就地修复的消息数（例如 `error` → `sent`）。与 `inserted` 一样需要触发 UI 刷新。 */
  updated: number
  errorI18nKey: string | null
}

export interface CelebrationEvent {
  kind: 'level_upgrade' | 'level_restore' | 'level_reset' | 'streak_warning' | 'makeup_granted' | 'makeup_used'
  payload: Record<string, unknown>
}

export interface ToastEvent {
  level: 'info' | 'success' | 'warn' | 'error'
  i18nKey: string
  params?: Record<string, string | number>
  /**
   * 已本地化的完整文案。存在时**优先于** `i18nKey` 展示。
   *
   * 用于「文案由主进程在运行时拼装」的场景（如服务端返回的 `fallbackText`、
   * 已是成品的通知正文），避免为了套 i18n 而做二次拼接。
   * NFR-10 仍然满足：静态 UI 文案一律走 i18n 资源文件。
   */
  text?: string
}

export interface AuthChangedEvent {
  authenticated: boolean
  userId: string | null
  reason: 'login' | 'logout' | 'expired' | 'kicked' | null
}

export interface NavigateEvent {
  to: string
  params?: Record<string, string>
}

export interface DraftImagesChangedEvent {
  images: ChatAttachment[]
}

/**
 * `settings:setShortcut` 的返回。
 *
 * ⚠️ 不能声明成 `ShortcutStatus`：主进程实际返回 `{ status, ok, errorI18nKey }`，
 * 声明成 `ShortcutStatus` 会让调用方拿到 `undefined` 却不报错——
 * 正是 PRD 反复警告的「静默取默认值」类缺陷。
 */
export interface ShortcutUpdateResult {
  status: ShortcutStatus
  ok: boolean
  errorI18nKey: string | null
}

export interface ShortcutStatus {
  enabled: boolean
  /** EDGE-L8：Wayland 下 globalShortcut 通常失效。 */
  available: boolean
  sessionType: 'x11' | 'wayland' | 'unknown'
  registered: string | null
  errorI18nKey: string | null
}

export interface AboutInfo {
  version: string
  electronVersion: string
  chromeVersion: string
  nodeVersion: string
  apiBaseUrl: string
  wsBaseUrl: string
  deviceId: string
  logsDir: string
  dataDir: string
  configDir: string
  credentialStorage: 'safeStorage' | 'plaintext-0600' | 'none'
  databasePath: string
}

export interface PlatformInfo {
  platform: string
  sessionType: 'x11' | 'wayland' | 'unknown'
  trayAvailable: boolean
  isGnome: boolean
  scaleFactor: number
  reducedMotion: boolean
}

export interface AttachmentUsage {
  count: number
  bytes: number
}

/** 互动发送结果 + 客户端需执行的副作用（§7.1a）。 */
export interface InteractionSendResult {
  data: InteractionSendData | null
  /** 业务失败码（40201/40202/40204…），成功为 null。 */
  code: number | null
  /** 成功时需要把被摘走的消息批量标记为已送达（EDGE-L22）。 */
  mergedRequestIds: string[]
}

/* -------------------------------------------------------------------------- */
/* 渲染进程可见的 API 形状                                                       */
/* -------------------------------------------------------------------------- */

export interface TksApi {
  auth: {
    login(username: string, password: string): Promise<AuthTokens>
    logout(clearLocalData: boolean): Promise<void>
    status(): Promise<{ authenticated: boolean; userId: string | null; deviceId: string }>
    getDeviceId(): Promise<string>
    storageMode(): Promise<'safeStorage' | 'plaintext-0600' | 'none'>
    onChanged(cb: (e: AuthChangedEvent) => void): () => void
    onExpired(cb: (e: { reason: string; kicked: boolean }) => void): () => void
  }
  connection: {
    getState(): Promise<ConnectionState>
    reconnect(manual: boolean): Promise<void>
    onState(cb: (s: ConnectionState) => void): () => void
    onTyping(cb: (e: TypingEvent) => void): () => void
    onQueued(cb: (e: { requestId: string; debounceWindowSec: number }) => void): () => void
    onRestFallback(cb: (e: { degraded: boolean }) => void): () => void
  }
  chat: {
    send(input: { requestId: string; content: string; attachmentIds: string[] }): Promise<void>
    sendViaRest(input: { requestId: string; content: string }): Promise<{ reply: string; message: ChatMessage[] }>
    listMessages(opts: { limit: number; beforeTimestamp?: number }): Promise<ChatMessage[]>
    loadOlder(opts: { beforeTimestamp: number; limit: number }): Promise<ChatMessage[]>
    deleteMessage(messageId: string): Promise<void>
    /** FR-CHAT-12：返回清空条数与推进后的同步游标。 */
    clearConversation(): Promise<{ deleted: number; cursor: number }>
    retryMessage(messageId: string): Promise<void>
    search(keyword: string, limit?: number): Promise<ChatMessage[]>
    pendingState(): Promise<{ typing: boolean; stage?: string; queuedRequestIds: string[] }>
    onMessagesUpdated(cb: (e: { messages: ChatMessage[]; removedIds: string[] }) => void): () => void
    onStreamDelta(cb: (e: StreamDeltaEvent) => void): () => void
    onStreamDone(cb: (e: StreamDoneEvent) => void): () => void
    onSyncStatus(cb: (e: SyncStatusEvent) => void): () => void
  }
  images: {
    pick(): Promise<ChatAttachment[]>
    addPaths(paths: string[]): Promise<ChatAttachment[]>
    readClipboard(): Promise<ChatAttachment[]>
    remove(attachmentId: string): Promise<void>
    listDraft(): Promise<ChatAttachment[]>
    clearDraft(): Promise<void>
    saveAs(localPath: string): Promise<{ saved: boolean; path: string | null }>
    openExternal(localPath: string): Promise<void>
    usage(): Promise<AttachmentUsage>
    cleanup(olderThanMs: number): Promise<{ removed: number; freedBytes: number }>
    onChanged(cb: (e: DraftImagesChangedEvent) => void): () => void
  }
  sync: {
    syncHistory(full: boolean): Promise<SyncStatusEvent>
  }
  history: {
    listNotifications(limit?: number): Promise<BotNotification[]>
    markRead(ids: string[]): Promise<void>
    markAllRead(): Promise<void>
    listFacts(limit?: number): Promise<UserFact[]>
    onNotificationsUpdated(cb: (e: { unread: number }) => void): () => void
    onFactsUpdated(cb: (e: { facts: UserFact[] }) => void): () => void
  }
  reminders: {
    list(): Promise<ReminderRecord[]>
    cancel(reminderId: string): Promise<void>
    create(input: { target: string; text: string }): Promise<ReminderRecord>
    onChanged(cb: (e: { reminders: ReminderRecord[] }) => void): () => void
  }
  settings: {
    get(): Promise<AppSettings>
    update(patch: SettingsPatch): Promise<AppSettings>
    testConnection(apiBaseUrl: string): Promise<{ ok: boolean; latencyMs: number | null; errorI18nKey: string | null }>
    getCity(): Promise<string>
    setCity(city: string): Promise<string>
    setAutostart(enabled: boolean, hidden: boolean): Promise<boolean>
    setShortcut(accelerator: string, enabled: boolean): Promise<ShortcutUpdateResult>
    shortcutStatus(): Promise<ShortcutStatus>
    about(): Promise<AboutInfo>
    export(format: 'json' | 'text'): Promise<{ saved: boolean; path: string | null; count: number }>
    openLogs(): Promise<void>
    openDataDir(): Promise<void>
  }
  gamification: {
    overview(): Promise<PointsOverviewData>
    levelStatus(): Promise<LevelStatus>
    levelConfig(): Promise<LevelConfigData>
    pointsHistory(opts: { page: number; pageSize: number; reasonCode?: string | null }): Promise<PointsHistoryData>
    balance(): Promise<{ balance: number; updatedAt: number }>
    interactionItems(): Promise<InteractionItemsData>
    interactionSend(itemId: string, requestId: string, text?: string): Promise<InteractionSendResult>
    makeupCard(): Promise<MakeupCardSummary>
    makeupCandidates(limit?: number): Promise<MakeupCandidatesData>
    makeupUse(targetDate: string): Promise<{ ok: boolean; code: number | null; data: MakeupCardUseData | null }>
    makeupHistory(opts: { page: number; pageSize: number }): Promise<MakeupCardHistoryData>
    cachedProgress(): Promise<UserProgressCache | null>
    onPointsChanged(cb: (e: { balance: number; entries: PointsLedgerEntry[] }) => void): () => void
    onLevelChanged(cb: (e: CelebrationEvent) => void): () => void
    onStreakWarning(cb: (e: CelebrationEvent) => void): () => void
    onMakeupCardChanged(cb: (e: { available: number; reason: string }) => void): () => void
    onCelebration(cb: (e: CelebrationEvent) => void): () => void
  }
  desktop: {
    showWindow(): Promise<void>
    hideWindow(): Promise<void>
    setBadge(count: number): Promise<void>
    setTheme(theme: 'system' | 'light' | 'dark'): Promise<void>
    getPlatform(): Promise<PlatformInfo>
    openExternal(url: string): Promise<void>
    onSystemThemeChanged(cb: (e: { dark: boolean }) => void): () => void
    onFocusInput(cb: () => void): () => void
    onNavigate(cb: (e: NavigateEvent) => void): () => void
    onToast(cb: (e: ToastEvent) => void): () => void
  }
}
