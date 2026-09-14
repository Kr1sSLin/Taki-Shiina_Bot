/**
 * 消息列表（FR-CHAT-13 / FR-CHAT-16 / FR-SYNC-8 / FR-CHAT-17）。
 *
 * - 「虚拟滚动」采用 CSS `content-visibility: auto`（见 `.message-row`），
 *   视口外的行跳过布局与绘制，避免长列表卡顿（FR-CHAT-16）
 * - 新消息到达时自动滚到底部；用户主动向上滚动后不再抢滚动位置
 * - 滚到顶部自动分页加载更早的消息，并保持视口锚点不跳动（FR-CHAT-16）
 * - 日期分隔线；服务端 `──── 新的一天 ────` 分隔标记渲染为分隔线而非气泡（FR-SYNC-8）
 * - 同一分钟内的连续同角色消息合并展示时间（FR-CHAT-13）
 * - `highlightId`（通知点击 / 搜索跳转）滚动定位并高亮（FR-NOTI-4 / FR-CHAT-17）
 */

import { useEffect, useRef } from 'react'
import type { ChatAttachment, ChatMessage } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { useAppStore } from '../../store/app-store'
import { useChatStore } from '../../store/chat-store'
import { useTranslation } from '../../i18n'
import { Button, EmptyState } from '../../components/primitives'
import { IconChat } from '../../components/Icons'
import { useReducedMotion } from '../../components/visual'
import { formatMonthDay, isSameLocalDay, sameMinute, weekdayShort } from '../../lib/format'
import { MessageBubble } from './MessageBubble'

const AT_BOTTOM_THRESHOLD = 56
const LOAD_OLDER_THRESHOLD = 24

/** `querySelector` 用的属性选择器（messageId 可能含特殊字符）。 */
function selectorFor(messageId: string): string {
  const escaped = typeof CSS !== 'undefined' && typeof CSS.escape === 'function' ? CSS.escape(messageId) : messageId.replace(/"/g, '\\"')
  return `[data-message-id="${escaped}"]`
}

/** 流式占位行的本地镜像（终稿由 `evt:messagesUpdated` 落地，占位只用于展示）。 */
function streamingPlaceholder(requestId: string, content: string): ChatMessage {
  return {
    messageId: `pending_${requestId}`,
    sessionId: '',
    role: 'bot',
    messageType: 'text',
    contentType: 'text',
    modelProvider: null,
    content,
    status: 'streaming',
    timestamp: Date.now(),
    errorCode: null
  }
}

export interface MessageListProps {
  /** 需滚动定位并高亮的消息 ID。 */
  highlightId: string | null
  /** 定位完成后回调（父级据此清空 `scrollTarget`）。 */
  onTargetHandled: () => void
  onRetry: (message: ChatMessage) => void
  onOpenImage: (attachment: ChatAttachment, siblings: ChatAttachment[]) => void
}

export function MessageList({ highlightId, onTargetHandled, onRetry, onOpenImage }: MessageListProps): JSX.Element {
  const { t } = useTranslation()
  const messages = useChatStore((s) => s.messages)
  const streaming = useChatStore((s) => s.streaming)
  const loaded = useChatStore((s) => s.loaded)
  const hasMore = useChatStore((s) => s.hasMore)
  const loadingOlder = useChatStore((s) => s.loadingOlder)
  const loadOlder = useChatStore((s) => s.loadOlder)
  const connected = useAppStore((s) => s.connection.status === 'connected')
  const reducedMotion = useReducedMotion()

  const listRef = useRef<HTMLDivElement | null>(null)
  const atBottomRef = useRef(true)
  /** 向上分页时的视口锚点（scrollHeight + 当时的消息条数）。 */
  const loadAnchorRef = useRef<{ scrollHeight: number; count: number } | null>(null)
  const countRef = useRef(0)
  const scrolledTargetRef = useRef<string | null>(null)

  const streamingText = Object.values(streaming).join('')
  const streamingLength = streamingText.length

  const scrollToBottom = (): void => {
    const el = listRef.current
    if (!el) return
    // 直接赋值（不带动画），因此不涉及 `prefers-reduced-motion`
    el.scrollTop = el.scrollHeight
  }

  // 首屏加载完成后直接落到最新一条
  useEffect(() => {
    if (loaded) {
      atBottomRef.current = true
      scrollToBottom()
    }
  }, [loaded])

  // 新消息到达：仅在用户本就贴底时跟随（不抢用户向上翻阅的位置）
  useEffect(() => {
    const grew = messages.length !== countRef.current
    countRef.current = messages.length
    if (grew && atBottomRef.current) scrollToBottom()
  }, [messages])

  // 流式增量导致最后一行变高时同样保持贴底
  useEffect(() => {
    if (atBottomRef.current) scrollToBottom()
  }, [streamingLength])

  // 分页加载后恢复视口锚点（避免用户正在看的消息被推走）。
  // ⚠️ 锚点必须在请求**结束后**消费掉：若那一页为空（`hasMore` 耗尽）或请求失败，
  //    messages 不变，锚点若留着会污染后续滚动的计算。
  useEffect(() => {
    const el = listRef.current
    const anchor = loadAnchorRef.current
    if (!el || anchor === null || loadingOlder) return
    loadAnchorRef.current = null
    if (messages.length > anchor.count) el.scrollTop = el.scrollHeight - anchor.scrollHeight
  }, [messages, loadingOlder])

  // FR-NOTI-4 / FR-CHAT-17：定位并高亮；目标消息可能尚未分页加载，故跟随 messages 变化重试
  useEffect(() => {
    if (!highlightId) {
      scrolledTargetRef.current = null
      return
    }
    if (scrolledTargetRef.current === highlightId) return
    const target = listRef.current?.querySelector<HTMLElement>(selectorFor(highlightId))
    if (!target) return
    scrolledTargetRef.current = highlightId
    atBottomRef.current = false
    target.scrollIntoView({ block: 'center', behavior: reducedMotion ? 'auto' : 'smooth' })
    // 高亮动画（CSS `target-pulse`）结束后清除
    const timer = window.setTimeout(() => onTargetHandled(), 3200)
    return () => window.clearTimeout(timer)
  }, [highlightId, messages, reducedMotion, onTargetHandled])

  /** FR-CHAT-16：向上分页（滚动到顶自动触发，也可点「加载更早的消息」）。 */
  const requestOlder = (): void => {
    const el = listRef.current
    if (!el) return
    loadAnchorRef.current = { scrollHeight: el.scrollHeight, count: messages.length }
    void loadOlder()
  }

  const handleScroll = (): void => {
    const el = listRef.current
    if (!el) return
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < AT_BOTTOM_THRESHOLD
    if (el.scrollTop <= LOAD_OLDER_THRESHOLD && hasMore && !loadingOlder) requestOlder()
  }

  const dayLabel = (timestamp: number): string => {
    const now = Date.now()
    if (isSameLocalDay(timestamp, now)) return t('common.today')
    if (isSameLocalDay(timestamp, now - 86_400_000)) return t('common.yesterday')
    return `${formatMonthDay(timestamp)} (${weekdayShort(timestamp)})`
  }

  const rows: JSX.Element[] = []
  let lastDayTimestamp: number | null = null
  let lastTimestamp: number | null = null
  let lastRole: ChatMessage['role'] | null = null

  for (const message of messages) {
    const isSeparator = message.role === 'system' || message.content.trim() === PROTOCOL.HISTORY_SEPARATOR
    if (isSeparator) {
      // FR-SYNC-8：服务端分隔标记 → 日期分割线，绝不渲染为普通气泡
      rows.push(
        <div className="day-separator" role="separator" aria-orientation="horizontal" key={`sep_${message.messageId}`}>
          {t('chat.daySeparator')}
        </div>
      )
      lastTimestamp = null
      continue
    }

    if (lastDayTimestamp === null || !isSameLocalDay(lastDayTimestamp, message.timestamp)) {
      rows.push(
        <div className="day-separator" role="separator" aria-orientation="horizontal" key={`day_${message.messageId}`}>
          {dayLabel(message.timestamp)}
        </div>
      )
      lastDayTimestamp = message.timestamp
      lastTimestamp = null
    }

    // FR-CHAT-13：同一分钟内的连续同角色消息只展示一次时间
    const showTime =
      lastTimestamp === null || lastRole !== message.role || !sameMinute(lastTimestamp, message.timestamp)
    lastTimestamp = message.timestamp
    lastRole = message.role

    rows.push(
      <MessageBubble
        key={message.messageId}
        message={message}
        showTime={showTime}
        highlight={message.messageId === highlightId}
        onRetry={onRetry}
        onOpenImage={onOpenImage}
      />
    )
  }

  const streamRows = Object.entries(streaming).map(([requestId, content]) => (
    <MessageBubble
      key={`stream_${requestId}`}
      message={streamingPlaceholder(requestId, content)}
      showTime={false}
      highlight={false}
      onRetry={onRetry}
      onOpenImage={onOpenImage}
    />
  ))

  const isEmpty = loaded && messages.length === 0 && streamRows.length === 0

  return (
    <div
      className="message-list"
      ref={listRef}
      onScroll={handleScroll}
      role="log"
      aria-label={t('chat.list.aria')}
      tabIndex={0}
    >
      {hasMore ? (
        <Button className="load-older" size="sm" variant="ghost" loading={loadingOlder} onClick={requestOlder}>
          {t('chat.loadOlder')}
        </Button>
      ) : messages.length > 0 ? (
        <p className="load-older muted">{t('chat.noMore')}</p>
      ) : null}

      {isEmpty ? (
        <EmptyState
          icon={<IconChat size={28} />}
          title={connected ? t('chat.empty') : t('chat.empty.offline')}
          hint={connected ? undefined : t('chat.notConnected')}
        />
      ) : null}

      {rows}
      {streamRows}
    </div>
  )
}

export default MessageList
