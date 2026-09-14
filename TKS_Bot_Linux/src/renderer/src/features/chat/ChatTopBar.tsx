/**
 * 聊天页顶栏（FR-UI-1 / FR-UI-2 / FR-CONN-5 / FR-CHAT-12）。
 *
 * - 联系人卡片 + 手绘涂鸦背景（FR-UI-2，`DoodleBackground` 的 8 种图形）
 * - 连接状态药丸：连接中 / 已连接 / 已断开 / 正在重连 / 正在续签登录 / 连接失败（FR-CONN-5）
 * - 断开时提供「重新连接」按钮（FR-CONN-5/6：手动重连会触发全量历史同步）
 * - 清空会话：与设置页共用 `chat.clearConversation` 同一实现，避免清空后被历史立即拉回
 *   （FR-CHAT-12 / FR-SET-4 明确要求两个入口共用实现）
 * - 搜索（FR-CHAT-17）与设置入口
 */

import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAppStore } from '../../store/app-store'
import { useChatStore } from '../../store/chat-store'
import { describeError, useTranslation } from '../../i18n'
import { Button, IconButton, Modal } from '../../components/primitives'
import { DoodleBackground } from '../../components/visual'
import { IconChevronLeft, IconRefresh, IconSearch, IconSettings, IconTrash } from '../../components/Icons'

/** 涂鸦 seed：按当前时段取，既稳定又随早晚变化（避免每帧抖动）。 */
function doodleSeed(): number {
  const now = new Date()
  return (now.getDate() + now.getHours()) % 8
}

export interface ChatTopBarProps {
  onOpenSearch: () => void
}

export function ChatTopBar({ onOpenSearch }: ChatTopBarProps): JSX.Element {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const connection = useAppStore((s) => s.connection)
  const pushToast = useAppStore((s) => s.pushToast)
  const [confirmClear, setConfirmClear] = useState(false)
  const [reconnecting, setReconnecting] = useState(false)

  const seed = useMemo(doodleSeed, [])
  const status = connection.status
  const connected = status === 'connected'

  // FR-CONN-5：六种状态文案（`unauthenticated` 属于过渡态，也给出明确文案）
  const statusKey = `conn.status.${status}`
  const statusClass =
    status === 'connected'
      ? 'is-connected'
      : status === 'disconnected' || status === 'degraded'
        ? 'is-disconnected'
        : 'is-pending'

  const handleReconnect = async (): Promise<void> => {
    setReconnecting(true)
    try {
      // FR-CONN-6：手动重连置 `manualReconnectPendingFullSync`，成功后全量同步
      await window.tks.connection.reconnect(true)
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setReconnecting(false)
    }
  }

  const handleClear = async (): Promise<void> => {
    setConfirmClear(false)
    try {
      await window.tks.chat.clearConversation()
      const chat = useChatStore.getState()
      chat.remove(chat.messages.map((message) => message.messageId))
      pushToast({ level: 'success', i18nKey: 'chat.clear.done' })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  return (
    <header className="chat-topbar">
      <DoodleBackground seed={seed} height={120} />

      <div className="chat-topbar-inner">
        <IconButton label={t('chat.back')} onClick={() => void navigate(-1)}>
          <IconChevronLeft size={18} />
        </IconButton>

        <span className="chat-avatar" role="img" aria-label={t('chat.avatar')}>
          🐼
        </span>

        <div className="chat-contact">
          <span className="chat-contact-name">{t('chat.title')}</span>
          <span className="chat-contact-sub">
            {connected ? t('chat.subtitle.online') : t('chat.subtitle.offline')}
          </span>
        </div>

        <span className={['chat-status-pill', statusClass].join(' ')} role="status" aria-live="polite">
          {t(statusKey)}
        </span>

        <div className="chat-topbar-actions">
          {!connected ? (
            <IconButton
              label={t('conn.reconnect')}
              title={t('conn.reconnect')}
              disabled={reconnecting}
              onClick={() => void handleReconnect()}
            >
              <IconRefresh size={18} />
            </IconButton>
          ) : null}
          <IconButton label={t('chat.search')} onClick={onOpenSearch}>
            <IconSearch size={18} />
          </IconButton>
          <IconButton label={t('chat.clear')} onClick={() => setConfirmClear(true)}>
            <IconTrash size={18} />
          </IconButton>
          <IconButton label={t('nav.settings')} onClick={() => void navigate('/settings')}>
            <IconSettings size={18} />
          </IconButton>
        </div>
      </div>

      {connection.degraded ? (
        <span className="muted">{t('conn.degraded.hint')}</span>
      ) : status === 'reconnecting' && connection.attempt > 0 ? (
        <span className="muted">{t('conn.reconnecting.attempt', { n: connection.attempt })}</span>
      ) : null}

      <Modal
        open={confirmClear}
        title={t('chat.clear')}
        onClose={() => setConfirmClear(false)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirmClear(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" onClick={() => void handleClear()}>
              {t('common.confirm')}
            </Button>
          </>
        }
      >
        <p className="muted">{t('chat.clear.confirm')}</p>
      </Modal>
    </header>
  )
}

export default ChatTopBar
