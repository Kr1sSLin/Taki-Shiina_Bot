/**
 * 聊天状态（§10.2 消息状态机、§10.3 流式时序）。
 *
 * 渲染端持有消息列表的镜像；主进程通过 `evt:messagesUpdated` / `evt:streamDelta` /
 * `evt:streamDone` 推送增量，渲染端只做 upsert/remove，不重新拉全量。
 */

import { create } from 'zustand'
import type { ChatMessage } from '@shared/protocol'
import type { StreamDoneEvent, SyncStatusEvent } from '@shared/ipc'

const PAGE_SIZE = 80
const OLDER_PAGE_SIZE = 50

export interface TypingState {
  typing: boolean
  stage?: 'vision' | 'generating' | 'interaction' | 'interaction_merge'
}

export interface QueuedState {
  requestId: string
  debounceWindowSec: number
}

export interface ChatState {
  loaded: boolean
  messages: ChatMessage[]
  /** `pending_{requestId}` → 累积内容（流式占位）。 */
  streaming: Record<string, string>
  typing: TypingState
  queued: QueuedState[]
  syncStatus: SyncStatusEvent | null
  hasMore: boolean
  loadingOlder: boolean
  /** 向上滚动分页的游标。 */
  oldestTimestamp: number | null
  searchKeyword: string
  searchResults: ChatMessage[] | null

  load: () => Promise<void>
  loadOlder: () => Promise<void>
  upsert: (messages: ChatMessage[]) => void
  remove: (ids: string[]) => void
  appendDelta: (requestId: string, delta: string) => void
  clearStreaming: (requestId: string) => void
  setTyping: (typing: TypingState) => void
  pushQueued: (queued: QueuedState) => void
  clearQueued: () => void
  setSyncStatus: (status: SyncStatusEvent) => void
  handleStreamDone: (event: StreamDoneEvent) => void
  reset: () => void
  search: (keyword: string) => Promise<void>
  clearSearch: () => void
  locate: (messageId: string) => void
  /** 需滚动定位到的消息 ID（FR-NOTI-4 / 搜索跳转）。 */
  scrollTarget: string | null
  setScrollTarget: (id: string | null) => void
}

/** 按 timestamp 排序并去重（`messageId` 为主键）。 */
function mergeMessages(current: ChatMessage[], incoming: ChatMessage[]): ChatMessage[] {
  const map = new Map<string, ChatMessage>()
  for (const m of current) map.set(m.messageId, m)
  for (const m of incoming) {
    const prev = map.get(m.messageId)
    // 保留附件（可能是本地已有的、而推送未带的）
    map.set(m.messageId, prev?.attachments?.length ? { ...m, attachments: prev.attachments } : m)
  }
  return [...map.values()].sort((a, b) => a.timestamp - b.timestamp)
}

export const useChatStore = create<ChatState>()((set, get) => ({
  loaded: false,
  messages: [],
  streaming: {},
  typing: { typing: false },
  queued: [],
  syncStatus: null,
  hasMore: true,
  loadingOlder: false,
  oldestTimestamp: null,
  searchKeyword: '',
  searchResults: null,
  scrollTarget: null,

  async load() {
    // FR-SYNC-1：启动即从本地 SQLite 加载历史并上屏，**不阻塞等待网络**
    const messages = await window.tks.chat.listMessages({ limit: PAGE_SIZE })
    set({
      messages,
      loaded: true,
      hasMore: messages.length >= PAGE_SIZE,
      oldestTimestamp: messages.length ? messages[0].timestamp : null
    })
  },

  async loadOlder() {
    const { loadingOlder, hasMore, oldestTimestamp, messages } = get()
    if (loadingOlder || !hasMore || oldestTimestamp === null) return
    set({ loadingOlder: true })
    try {
      const older = await window.tks.chat.loadOlder({ beforeTimestamp: oldestTimestamp, limit: OLDER_PAGE_SIZE })
      if (older.length === 0) {
        set({ hasMore: false })
        return
      }
      set({
        messages: mergeMessages(messages, older),
        hasMore: older.length >= OLDER_PAGE_SIZE,
        oldestTimestamp: older[0].timestamp
      })
    } finally {
      set({ loadingOlder: false })
    }
  },

  upsert(incoming) {
    if (incoming.length === 0) return
    set({ messages: mergeMessages(get().messages, incoming) })
  },

  remove(ids) {
    if (ids.length === 0) return
    const drop = new Set(ids)
    set({ messages: get().messages.filter((m) => !drop.has(m.messageId)) })
  },

  appendDelta(requestId, delta) {
    const streaming = { ...get().streaming }
    streaming[requestId] = (streaming[requestId] ?? '') + delta
    set({ streaming })
  },

  clearStreaming(requestId) {
    const streaming = { ...get().streaming }
    delete streaming[requestId]
    set({ streaming })
  },

  setTyping(typing) {
    set({ typing })
    if (!typing.typing) set({ queued: [] })
  },

  pushQueued(queued) {
    const existing = get().queued.filter((q) => q.requestId !== queued.requestId)
    set({ queued: [...existing, queued] })
  },

  clearQueued() {
    set({ queued: [] })
  },

  setSyncStatus(syncStatus) {
    set({ syncStatus })
  },

  handleStreamDone(event) {
    const streaming = { ...get().streaming }
    delete streaming[event.requestId]
    set({
      streaming,
      typing: { typing: false },
      queued: []
    })
  },

  reset() {
    set({
      loaded: false,
      messages: [],
      streaming: {},
      typing: { typing: false },
      queued: [],
      hasMore: true,
      oldestTimestamp: null,
      searchKeyword: '',
      searchResults: null,
      scrollTarget: null
    })
  },

  async search(keyword) {
    const term = keyword.trim()
    if (!term) {
      set({ searchKeyword: '', searchResults: null })
      return
    }
    const searchResults = await window.tks.chat.search(term, 200)
    set({ searchKeyword: term, searchResults })
  },

  clearSearch() {
    set({ searchKeyword: '', searchResults: null })
  },

  locate(messageId) {
    set({ scrollTarget: messageId })
  },

  setScrollTarget(scrollTarget) {
    set({ scrollTarget })
  }
}))
