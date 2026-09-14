/**
 * 全局应用状态（设置 / 连接 / 平台 / 主题 / 通知提示 / 草稿附件 / 未读）。
 *
 * 对标 Android 的 `StateFlow + UiState`：主进程负责权威状态，渲染端只做镜像与乐观更新。
 */

import { create } from 'zustand'
import type {
  AppSettings,
  ChatAttachment,
  ConnectionState,
  ReminderRecord,
  SettingsPatch
} from '@shared/protocol'
import type { AboutInfo, CelebrationEvent, PlatformInfo, ShortcutStatus, SyncStatusEvent, ToastEvent } from '@shared/ipc'

export type ToastItem = ToastEvent & { id: string }

export interface AppState {
  ready: boolean
  settings: AppSettings | null
  about: AboutInfo | null
  platform: PlatformInfo | null
  shortcut: ShortcutStatus | null
  connection: ConnectionState
  systemDark: boolean
  /** 由设置推导出的实际主题。 */
  theme: 'light' | 'dark'
  toasts: ToastItem[]
  celebration: CelebrationEvent | null
  reminders: ReminderRecord[]
  draftImages: ChatAttachment[]
  unreadNotifications: number
  /** FR-CHAT-9：降级态下开放 REST 单次请求模式。 */
  restFallback: boolean
  restFallbackEnabled: boolean
  syncStatus: SyncStatusEvent | null
  /** 窗口是否聚焦（FR-NOTI-2 的渲染端镜像，用于展示策略）。 */
  focused: boolean

  bootstrap: () => Promise<void>
  setSettings: (settings: AppSettings) => void
  updateSettings: (patch: SettingsPatch) => Promise<AppSettings>
  setConnection: (state: ConnectionState) => void
  setSystemDark: (dark: boolean) => void
  applyTheme: () => void
  pushToast: (toast: ToastEvent) => void
  dismissToast: (id: string) => void
  setCelebration: (event: CelebrationEvent | null) => void
  setReminders: (reminders: ReminderRecord[]) => void
  setDraftImages: (images: ChatAttachment[]) => void
  setUnread: (count: number) => void
  setRestFallback: (enabled: boolean, degraded: boolean) => void
  setSyncStatus: (status: SyncStatusEvent) => void
  setFocused: (focused: boolean) => void
  refreshShortcut: () => Promise<void>
}

function computeTheme(settings: AppSettings | null, systemDark: boolean): 'light' | 'dark' {
  const pref = settings?.theme ?? 'system'
  if (pref === 'light') return 'light'
  if (pref === 'dark') return 'dark'
  return systemDark ? 'dark' : 'light'
}

export const useAppStore = create<AppState>()((set, get) => ({
  ready: false,
  settings: null,
  about: null,
  platform: null,
  shortcut: null,
  connection: { status: 'unauthenticated', attempt: 0, lastError: null, degraded: false },
  systemDark: window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false,
  theme: 'light',
  toasts: [],
  celebration: null,
  reminders: [],
  draftImages: [],
  unreadNotifications: 0,
  restFallback: false,
  restFallbackEnabled: false,
  syncStatus: null,
  focused: true,

  async bootstrap() {
    const [settings, about, platform, shortcut, connection, reminders, draftImages] = await Promise.all([
      window.tks.settings.get(),
      window.tks.settings.about(),
      window.tks.desktop.getPlatform(),
      window.tks.settings.shortcutStatus(),
      window.tks.connection.getState(),
      window.tks.reminders.list(),
      window.tks.images.listDraft()
    ])
    set({
      settings,
      about,
      platform,
      shortcut,
      connection,
      reminders,
      draftImages,
      ready: true,
      theme: computeTheme(settings, get().systemDark)
    })
    get().applyTheme()
  },

  setSettings(settings) {
    set({ settings, theme: computeTheme(settings, get().systemDark) })
    get().applyTheme()
  },

  async updateSettings(patch) {
    const next = await window.tks.settings.update(patch)
    get().setSettings(next)
    return next
  },

  setConnection(connection) {
    set({ connection, restFallback: connection.degraded })
  },

  setSystemDark(systemDark) {
    set({ systemDark, theme: computeTheme(get().settings, systemDark) })
    get().applyTheme()
  },

  /** FR-SET-1：把主题写到 `<html data-theme>`，CSS 变量据此切换。 */
  applyTheme() {
    const { theme } = get()
    document.documentElement.dataset.theme = theme
    document.documentElement.style.colorScheme = theme
  },

  pushToast(toast) {
    const id = `toast_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`
    set({ toasts: [...get().toasts, { ...toast, id }] })
    // 4.5 秒自动消失
    setTimeout(() => get().dismissToast(id), 4500)
  },

  dismissToast(id) {
    set({ toasts: get().toasts.filter((item) => item.id !== id) })
  },

  setCelebration(celebration) {
    set({ celebration })
  },

  setReminders(reminders) {
    set({ reminders })
  },

  setDraftImages(draftImages) {
    set({ draftImages })
  },

  setUnread(unreadNotifications) {
    set({ unreadNotifications })
    // FR-DSK-1 / FR-CONN-9：托盘角标同步
    void window.tks.desktop.setBadge(unreadNotifications)
  },

  setRestFallback(restFallbackEnabled, degraded) {
    set({ restFallbackEnabled, restFallback: degraded })
  },

  setSyncStatus(syncStatus) {
    set({ syncStatus })
  },

  setFocused(focused) {
    set({ focused })
  },

  async refreshShortcut() {
    set({ shortcut: await window.tks.settings.shortcutStatus() })
  }
}))
