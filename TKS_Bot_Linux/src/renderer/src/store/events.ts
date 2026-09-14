/**
 * 主进程 → 渲染进程 事件订阅（单一入口）。
 *
 * 在 `main.tsx` 里于渲染之前调用一次 `wireEvents()`；返回的清理函数在卸载时调用。
 * 所有订阅都返回取消函数，避免热重载（HMR）时重复累积监听器。
 */

import { IPC, type CelebrationEvent, type NavigateEvent, type StreamDoneEvent, type StreamDeltaEvent, type SyncStatusEvent, type ToastEvent, type TypingEvent } from '@shared/ipc'
import type { ChatMessage, PointsLedgerEntry } from '@shared/protocol'
import { useAppStore } from './app-store'
import { useAuthStore } from './auth-store'
import { useChatStore } from './chat-store'
import { useProfileStore } from './profile-store'

type Unsubscribe = () => void

export function wireEvents(): Unsubscribe {
  const unsubs: Unsubscribe[] = []
  const app = useAppStore.getState
  const chat = useChatStore.getState
  const profile = useProfileStore.getState

  /* ---------------------------------- 认证 -------------------------------- */

  unsubs.push(
    window.tks.auth.onChanged((event) => {
      useAuthStore.setState({
        authenticated: event.authenticated,
        userId: event.userId,
        ...(event.reason === 'logout' || event.reason === 'expired' || event.reason === 'kicked'
          ? { expiryNotice: null }
          : {})
      })
      if (!event.authenticated) {
        // FR-AUTH-7：退出登录后清空渲染端缓存，避免串号
        chat().reset()
        profile().reset()
      }
    })
  )

  unsubs.push(
    window.tks.auth.onExpired((event) => {
      // EDGE-L3：区分「被静默顶号」与「登录超期」
      useAuthStore.getState().setExpiryNotice({ reason: event.reason, kicked: event.kicked })
      chat().reset()
      profile().reset()
    })
  )

  /* ---------------------------------- 连接 -------------------------------- */

  unsubs.push(window.tks.connection.onState((state) => app().setConnection(state)))
  unsubs.push(window.tks.connection.onTyping((event: TypingEvent) => chat().setTyping(event)))
  unsubs.push(
    window.tks.connection.onQueued((event) => {
      // EDGE-L17：`debounceWindowSec` 一律用服务端下发值，不在客户端硬编码 8/20
      chat().pushQueued(event)
    })
  )
  unsubs.push(
    window.tks.connection.onRestFallback((event) => {
      // FR-CHAT-9：降级态才开放「单次请求模式」
      app().setRestFallback(event.degraded, event.degraded)
    })
  )

  /* ---------------------------------- 聊天 -------------------------------- */

  unsubs.push(
    window.tks.chat.onMessagesUpdated((event: { messages: ChatMessage[]; removedIds: string[] }) => {
      if (event.removedIds.length) chat().remove(event.removedIds)
      if (event.messages.length) chat().upsert(event.messages)
    })
  )

  unsubs.push(
    window.tks.chat.onStreamDelta((event: StreamDeltaEvent) => {
      // FR-CHAT-5：渲染端只累积文本用于占位气泡展示，最终内容以 `streamDone` 为准
      chat().appendDelta(event.requestId, event.delta)
    })
  )

  unsubs.push(
    window.tks.chat.onStreamDone((event: StreamDoneEvent & { errorCode?: string }) => {
      chat().handleStreamDone(event)
      chat().clearStreaming(event.requestId)
      // 最终多气泡由 `evt:messagesUpdated` 推入；这里只需把占位清掉
      if (event.errorCode) {
        const key = event.errorCode === 'TIMEOUT' ? 'error.client.TIMEOUT' : 'error.client.CONNECTION_LOST'
        app().pushToast({ level: 'error', i18nKey: key })
      }
      // FR-REM：提醒已由主进程排程，这里不做二次处理（FR-REM-1）
    })
  )

  unsubs.push(
    window.tks.chat.onSyncStatus((status: SyncStatusEvent) => {
      chat().setSyncStatus(status)
      app().setSyncStatus(status)
      // FR-SYNC-6：同步失败必须在 UI 可见
      if (!status.ok) {
        app().pushToast({ level: 'error', i18nKey: status.errorI18nKey ?? 'sync.failed' })
      }
    })
  )

  /* ---------------------------------- 图片 -------------------------------- */

  unsubs.push(window.tks.images.onChanged((event) => app().setDraftImages(event.images)))

  /* -------------------------------- 历史与记忆 ----------------------------- */

  unsubs.push(
    window.tks.history.onNotificationsUpdated((event) => {
      app().setUnread(event.unread)
    })
  )

  /* ---------------------------------- 提醒 -------------------------------- */

  unsubs.push(window.tks.reminders.onChanged((event) => app().setReminders(event.reminders)))

  /* ---------------------------- 积分 / 等级 / 互动 ------------------------- */

  unsubs.push(
    window.tks.gamification.onPointsChanged((event: { balance: number; entries: PointsLedgerEntry[] }) => {
      profile().applyPointsChanged(event.balance, event.entries)
    })
  )

  unsubs.push(
    window.tks.gamification.onLevelChanged(() => {
      // 等级变化后刷新总览/阈值（事件载荷已由主进程转为 celebration/toast）
      void profile().refreshOverview()
      void profile().refreshLevelConfig()
    })
  )

  unsubs.push(
    window.tks.gamification.onStreakWarning(() => {
      void profile().refreshOverview()
    })
  )

  unsubs.push(
    window.tks.gamification.onMakeupCardChanged((event: { available: number; reason: string }) => {
      // FR-MC-8：实时同步，无需轮询
      profile().applyMakeupAvailable(event.available)
      void profile().refreshMakeup()
    })
  )

  unsubs.push(
    window.tks.gamification.onCelebration((event: CelebrationEvent) => {
      app().setCelebration(event)
    })
  )

  /* ---------------------------------- 桌面 -------------------------------- */

  unsubs.push(
    window.tks.desktop.onSystemThemeChanged((event) => {
      if (useAppStore.getState().settings?.theme === 'system') app().setSystemDark(event.dark)
    })
  )

  unsubs.push(
    window.tks.desktop.onToast((event: ToastEvent) => {
      app().pushToast(event)
    })
  )

  /* ------------------------------- 导航 / 焦点 ---------------------------- */

  unsubs.push(
    window.tks.desktop.onNavigate((event: NavigateEvent) => {
      if (!event?.to) return
      window.location.hash = `#${event.to}`
      // FR-NOTI-4：点击通知后滚动定位到对应消息
      const messageId = event.params?.messageId
      if (messageId) {
        chat().setScrollTarget(messageId)
        chat().locate(messageId)
      }
    })
  )

  unsubs.push(
    window.tks.desktop.onFocusInput(() => {
      // FR-DSK-2 / FR-DSK-6：唤起并聚焦输入框
      window.dispatchEvent(new CustomEvent('tks:focus-input'))
    })
  )

  /* --------------------------- 焦点状态（FR-NOTI-2 镜像） ----------------- */

  const onFocus = (): void => app().setFocused(true)
  const onBlur = (): void => app().setFocused(false)
  window.addEventListener('focus', onFocus)
  window.addEventListener('blur', onBlur)
  unsubs.push(() => {
    window.removeEventListener('focus', onFocus)
    window.removeEventListener('blur', onBlur)
  })

  /* ---------------------- 系统「减少动画」偏好（NFR-11） ------------------- */

  const motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  const applyMotion = (): void => {
    document.documentElement.dataset.reducedMotion = motionQuery.matches ? 'true' : 'false'
  }
  applyMotion()
  motionQuery.addEventListener('change', applyMotion)
  unsubs.push(() => motionQuery.removeEventListener('change', applyMotion))

  /* ------------------------------ 键盘快捷键 ----------------------------- */

  const onKeyDown = (event: KeyboardEvent): void => {
    const target = event.target as HTMLElement | null
    const inField =
      target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA' || target?.isContentEditable === true

    // FR-DSK-6：Esc 收回托盘、Ctrl+F 搜索、Ctrl+, 设置、Ctrl+L 聚焦输入框、Ctrl+Shift+R 手动重连
    if (event.key === 'Escape') {
      window.dispatchEvent(new CustomEvent('tks:escape'))
      return
    }
    if (!(event.ctrlKey || event.metaKey)) return

    const key = event.key.toLowerCase()
    if (key === 'f') {
      event.preventDefault()
      window.dispatchEvent(new CustomEvent('tks:search'))
      return
    }
    if (key === ',') {
      event.preventDefault()
      window.location.hash = '#/settings'
      return
    }
    if (key === 'l') {
      event.preventDefault()
      window.dispatchEvent(new CustomEvent('tks:focus-input'))
      return
    }
    if (key === 'r' && event.shiftKey) {
      // FR-CONN-6：手动重连（含全量同步）
      event.preventDefault()
      void window.tks.connection.reconnect(true)
      return
    }
    // Ctrl+V 粘贴图片：交给 Composer 处理，这里不拦截
    void inField
  }

  window.addEventListener('keydown', onKeyDown)
  unsubs.push(() => window.removeEventListener('keydown', onKeyDown))

  return () => {
    for (const unsub of unsubs) {
      try {
        unsub()
      } catch {
        /* ignore */
      }
    }
  }
}

export { IPC }
