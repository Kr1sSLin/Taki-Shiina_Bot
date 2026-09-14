/**
 * 全文搜索浮层（FR-CHAT-17）。
 *
 * - 搜索走主进程的 SQLite FTS5（`useChatStore.search`），渲染端不自己扫全量消息
 * - 关键词高亮（`.search-hit`）+ 命中上下文截断
 * - 点击结果 → `locate(messageId)`，由 `MessageList` 滚动定位并高亮（FR-NOTI-4 同一条路径）
 * - 覆盖在聊天区之上（`.search-overlay` 绝对定位），Esc 或关闭按钮退出
 */

import { useEffect, useRef, useState } from 'react'
import { useChatStore } from '../../store/chat-store'
import { useTranslation } from '../../i18n'
import { IconButton, Input } from '../../components/primitives'
import { IconClose, IconSearch } from '../../components/Icons'
import { formatDateTime } from '../../lib/format'

const DEBOUNCE_MS = 280
const SNIPPET_RADIUS = 32
const SNIPPET_LENGTH = 120

/** 命中上下文截断（避免把整段长消息铺满列表）。 */
function snippet(content: string, keyword: string): string {
  if (!keyword) return content.slice(0, SNIPPET_LENGTH)
  const index = content.toLowerCase().indexOf(keyword.toLowerCase())
  if (index < 0) return content.slice(0, SNIPPET_LENGTH)
  const start = Math.max(0, index - SNIPPET_RADIUS)
  const end = Math.min(content.length, start + SNIPPET_LENGTH)
  return `${start > 0 ? '…' : ''}${content.slice(start, end)}${end < content.length ? '…' : ''}`
}

/** 关键词高亮：把命中片段包成 `<mark class="search-hit">`。 */
function highlight(text: string, keyword: string): JSX.Element[] {
  if (!keyword) return [<span key="plain">{text}</span>]
  const lowerText = text.toLowerCase()
  const lowerKeyword = keyword.toLowerCase()
  const parts: JSX.Element[] = []
  let cursor = 0
  let key = 0
  while (cursor < text.length) {
    const found = lowerText.indexOf(lowerKeyword, cursor)
    if (found < 0) {
      parts.push(<span key={key++}>{text.slice(cursor)}</span>)
      break
    }
    if (found > cursor) parts.push(<span key={key++}>{text.slice(cursor, found)}</span>)
    parts.push(
      <mark className="search-hit" key={key++}>
        {text.slice(found, found + lowerKeyword.length)}
      </mark>
    )
    cursor = found + lowerKeyword.length
  }
  return parts
}

export interface SearchOverlayProps {
  open: boolean
  onClose: () => void
}

export function SearchOverlay({ open, onClose }: SearchOverlayProps): JSX.Element | null {
  const { t } = useTranslation()
  const results = useChatStore((s) => s.searchResults)
  const searchKeyword = useChatStore((s) => s.searchKeyword)
  const search = useChatStore((s) => s.search)
  const locate = useChatStore((s) => s.locate)
  const clearSearch = useChatStore((s) => s.clearSearch)
  const [keyword, setKeyword] = useState('')
  const inputRef = useRef<HTMLInputElement | null>(null)

  // 打开时聚焦输入框（NFR-11）
  useEffect(() => {
    if (!open) return
    const timer = window.setTimeout(() => inputRef.current?.focus(), 30)
    return () => window.clearTimeout(timer)
  }, [open])

  // 关闭时清空关键词与结果，避免下次打开看到过期结果
  useEffect(() => {
    if (open) return
    setKeyword('')
    clearSearch()
  }, [open, clearSearch])

  // 输入防抖（避免每次按键都打一次 FTS）
  useEffect(() => {
    if (!open) return
    const timer = window.setTimeout(() => {
      void search(keyword)
    }, DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keyword, open, search])

  // Esc 关闭；同时响应主进程分发的 `tks:escape`
  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onClose()
      }
    }
    window.addEventListener('keydown', onKey)
    window.addEventListener('tks:escape', onClose)
    return () => {
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('tks:escape', onClose)
    }
  }, [open, onClose])

  if (!open) return null

  const term = searchKeyword || keyword.trim()

  const jump = (messageId: string): void => {
    // FR-CHAT-17：跳转定位（MessageList 依据 scrollTarget 滚动 + 高亮）
    locate(messageId)
    onClose()
  }

  return (
    <div className="search-overlay" role="dialog" aria-modal="true" aria-label={t('chat.search')}>
      <div className="search-overlay-head">
        <Input
          ref={inputRef}
          className="search-input"
          value={keyword}
          placeholder={t('chat.search.placeholder')}
          aria-label={t('chat.search')}
          onChange={(event) => setKeyword(event.target.value)}
        />
        <span className="muted" role="status" aria-live="polite">
          {results ? t('chat.search.resultCount', { n: results.length }) : ''}
        </span>
        <IconButton label={t('chat.search.close')} onClick={onClose}>
          <IconClose size={18} />
        </IconButton>
      </div>

      <div className="search-results">
        {results === null ? (
          <p className="muted">
            <IconSearch size={14} /> {t('chat.search.idle')}
          </p>
        ) : results.length === 0 ? (
          <p className="muted">{t('chat.search.noResult')}</p>
        ) : (
          results.map((message) => (
            <button
              key={message.messageId}
              type="button"
              className="search-result"
              aria-label={t('chat.search.jump')}
              onClick={() => jump(message.messageId)}
            >
              <span className="search-result-meta">
                {message.role === 'user' ? t('chat.role.user') : t('chat.role.bot')} · {formatDateTime(message.timestamp)}
              </span>
              <span>{highlight(snippet(message.content, term), term)}</span>
            </button>
          ))
        )}
      </div>
    </div>
  )
}

export default SearchOverlay
