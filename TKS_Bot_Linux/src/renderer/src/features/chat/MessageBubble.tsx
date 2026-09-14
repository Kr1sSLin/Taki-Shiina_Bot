/**
 * 单条消息气泡（含互动礼物标记与右键菜单）。
 *
 * FR-CHAT-3  发送状态：sending（时钟）/ sent（单勾）/ received（双勾）/ error（警告 + 文案）
 * FR-CHAT-5  流式占位：`status === 'streaming'` 时由 CSS 追加光标
 * FR-CHAT-11 右键菜单：复制 / 删除（仅本地）/ 重试（仅用户消息，回填输入框）
 * FR-CHAT-13 时间合并由父级（MessageList）通过 `showTime` 决定
 * FR-INT-6   互动回复复用本组件，用 `interactionItemIcon` / `interactionItemName` / `messageKind`
 *            加轻量礼物标记（messageKind === 'interaction_failed' 时明确标注失败）
 * FR-SYNC-9  剥离历史内容里可能残留的 `【MM-DD HH:MM】` 前缀
 * FR-IMG-9   附件以缩略图呈现，点击打开大图查看器
 *
 * NFR-11：气泡可 Tab 聚焦；键盘 ContextMenu / Shift+F10 也能唤出菜单。
 */

import { useEffect, useState } from 'react'
import type { ChatAttachment, ChatMessage, MessageStatus } from '@shared/protocol'
import { useAppStore } from '../../store/app-store'
import { useChatStore } from '../../store/chat-store'
import { describeError, useTranslation } from '../../i18n'
import { Modal, Button, Spinner } from '../../components/primitives'
import { IconCheck, IconClock, IconCopy, IconRefresh, IconTrash, IconWarning } from '../../components/Icons'
import { formatDateTime, formatTime } from '../../lib/format'
import { attachmentSrc } from './ImageViewer'

/** FR-SYNC-9：仅用于喂给模型的历史前缀，理论上不入库，渲染时兜底剥离。 */
const HISTORY_PREFIX = /^【\d{2}-\d{2} \d{2}:\d{2}】\s*/

const MENU_WIDTH = 184
const MENU_HEIGHT_PER_ITEM = 32
const MENU_HEIGHT_BASE = 12

/** 复制到剪贴板（`navigator.clipboard` 不可用时回退到临时 textarea）。 */
async function copyText(text: string): Promise<void> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
      return
    }
  } catch {
    /* 回退到 execCommand */
  }
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', 'readonly')
  area.style.position = 'fixed'
  area.style.top = '-1000px'
  area.style.opacity = '0'
  document.body.appendChild(area)
  area.select()
  document.execCommand('copy')
  document.body.removeChild(area)
}

export interface MessageBubbleProps {
  message: ChatMessage
  /** FR-CHAT-13：同一分钟内的连续消息只显示一次时间。 */
  showTime: boolean
  /** FR-NOTI-4 / FR-CHAT-17：命中搜索或通知定位时高亮。 */
  highlight: boolean
  onRetry: (message: ChatMessage) => void
  onOpenImage: (attachment: ChatAttachment, siblings: ChatAttachment[]) => void
}

export function MessageBubble({ message, showTime, highlight, onRetry, onOpenImage }: MessageBubbleProps): JSX.Element {
  const { t } = useTranslation()
  const pushToast = useAppStore((s) => s.pushToast)
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const isUser = message.role === 'user'
  const attachments = message.attachments ?? []
  const content = message.content.replace(HISTORY_PREFIX, '')

  const hasGift =
    message.messageKind === 'interaction' ||
    message.messageKind === 'interaction_failed' ||
    Boolean(message.interactionItemName) ||
    Boolean(message.interactionItemIcon)
  const giftFailed = message.messageKind === 'interaction_failed'
  const giftName = message.interactionItemName || t('chat.gift.unknown')

  // 菜单：点击任意处 / 滚动 / Esc 关闭（NFR-11）
  useEffect(() => {
    if (!menu) return
    const close = (): void => setMenu(null)
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') close()
    }
    window.addEventListener('click', close)
    window.addEventListener('blur', close)
    window.addEventListener('scroll', close, true)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('click', close)
      window.removeEventListener('blur', close)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('keydown', onKey)
    }
  }, [menu])

  const openMenuAt = (x: number, y: number): void => {
    const itemCount = isUser ? 3 : 2
    const height = MENU_HEIGHT_BASE + MENU_HEIGHT_PER_ITEM * itemCount
    setMenu({
      x: Math.max(8, Math.min(x, window.innerWidth - MENU_WIDTH - 8)),
      y: Math.max(8, Math.min(y, window.innerHeight - height - 8))
    })
  }

  const statusGlyph = (status: MessageStatus): JSX.Element | null => {
    switch (status) {
      case 'sending':
        return (
          <span className="message-status" role="img" aria-label={t('chat.status.sending')} title={t('chat.status.sending')}>
            <IconClock size={12} />
          </span>
        )
      case 'sent':
        return (
          <span className="message-status" role="img" aria-label={t('chat.status.sent')} title={t('chat.status.sent')}>
            <IconCheck size={12} />
          </span>
        )
      case 'received':
        // 双勾（FR-CHAT-7：可能对应被防抖合并的多条用户消息）
        return (
          <span className="message-status" role="img" aria-label={t('chat.status.received')} title={t('chat.status.received')}>
            <IconCheck size={12} />
            <IconCheck size={12} style={{ marginLeft: -5 }} />
          </span>
        )
      case 'streaming':
        return (
          <span className="message-status" role="img" aria-label={t('chat.status.streaming')}>
            <Spinner size={11} />
          </span>
        )
      case 'error':
        return (
          <span className="message-status is-error" role="img" aria-label={t('chat.status.error')} title={t('chat.status.error')}>
            <IconWarning size={12} />
          </span>
        )
      default:
        return null
    }
  }

  const handleCopy = async (): Promise<void> => {
    setMenu(null)
    await copyText(content)
    pushToast({ level: 'success', i18nKey: 'common.copied' })
  }

  const handleDelete = async (): Promise<void> => {
    setConfirmDelete(false)
    try {
      // 仅删除本地记录（FR-CHAT-11），服务端数据不动
      await window.tks.chat.deleteMessage(message.messageId)
      useChatStore.getState().remove([message.messageId])
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  const handleRetry = (): void => {
    setMenu(null)
    onRetry(message)
  }

  const rowClass = [
    'message-row',
    isUser ? 'is-user' : 'is-bot',
    message.status === 'error' ? 'is-error' : '',
    message.status === 'streaming' ? 'is-streaming' : '',
    highlight ? 'is-target' : ''
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <>
      <div className={rowClass} data-message-id={message.messageId}>
      <div
        className="message-bubble"
        role="article"
        tabIndex={0}
        aria-label={`${isUser ? t('chat.role.user') : t('chat.role.bot')} ${formatTime(message.timestamp)}`}
        onContextMenu={(event) => {
          event.preventDefault()
          openMenuAt(event.clientX, event.clientY)
        }}
        onKeyDown={(event) => {
          if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
            event.preventDefault()
            const rect = event.currentTarget.getBoundingClientRect()
            openMenuAt(rect.left + 24, rect.top + 24)
          }
        }}
      >
        {hasGift ? (
          <span className="gift-tag" role="note">
            <span aria-hidden="true">{message.interactionItemIcon || '🎁'}</span>
            {giftFailed ? t('chat.gift.failed') : giftName}
          </span>
        ) : null}

        {attachments.length > 0 ? (
          <div className="message-attachments">
            {attachments.map((attachment) => (
              <button
                key={attachment.attachmentId}
                type="button"
                className="attachment-thumb"
                aria-label={t('chat.image.viewLarge')}
                title={t('chat.image.viewLarge')}
                onClick={() => onOpenImage(attachment, attachments)}
              >
                <img src={attachmentSrc(attachment.localPath)} alt="" loading="lazy" draggable={false} />
              </button>
            ))}
          </div>
        ) : null}

        {content ? <span>{content}</span> : null}

        {message.status === 'error' ? (
          <p className="message-error" role="alert">
            {describeError({ appErrorCode: message.errorCode })}
          </p>
        ) : null}

        {showTime ? (
          <div className="message-meta">
            <time dateTime={new Date(message.timestamp).toISOString()} title={formatDateTime(message.timestamp)}>
              {formatTime(message.timestamp)}
            </time>
            {isUser ? statusGlyph(message.status) : null}
          </div>
        ) : null}
        </div>
      </div>

      {/*
        ⚠️ 菜单与确认弹窗必须渲染在 `.message-row` **之外**：
        该行有 `content-visibility: auto`（FR-CHAT-16），会带来 layout/paint containment，
        行内的 `position: fixed` 元素会被裁剪并相对该行定位。
      */}
      {menu ? (
        <div
          className="context-menu glass"
          role="menu"
          aria-label={t('chat.menu.aria')}
          style={{ left: menu.x, top: menu.y }}
        >
          <button type="button" role="menuitem" className="context-menu-item" onClick={() => void handleCopy()}>
            <IconCopy size={15} />
            {t('chat.menu.copy')}
          </button>
          <button
            type="button"
            role="menuitem"
            className="context-menu-item is-danger"
            onClick={() => {
              setMenu(null)
              setConfirmDelete(true)
            }}
          >
            <IconTrash size={15} />
            {t('chat.menu.delete')}
          </button>
          {isUser ? (
            <button type="button" role="menuitem" className="context-menu-item" onClick={handleRetry}>
              <IconRefresh size={15} />
              {t('chat.menu.retry')}
            </button>
          ) : null}
        </div>
      ) : null}

      <Modal
        open={confirmDelete}
        title={t('chat.menu.delete')}
        onClose={() => setConfirmDelete(false)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirmDelete(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" onClick={() => void handleDelete()}>
              {t('common.delete')}
            </Button>
          </>
        }
      >
        <p className="muted">{t('chat.menu.delete.confirm')}</p>
      </Modal>
    </>
  )
}

export default MessageBubble
