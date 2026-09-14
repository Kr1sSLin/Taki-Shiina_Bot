/**
 * 历史页（FR-HIS-1..4）。
 *
 * - 「Bot 通知」Tab：列表 + 标记已读 / 全部已读 + 未读数（FR-HIS-1）
 * - 「记忆档案」Tab：复用 `FactsWindow`（FR-HIS-2/3/4，只读）
 */

import { useCallback, useEffect, useState } from 'react'
import type { BotNotification } from '@shared/protocol'
import { errorI18nKey } from '@shared/errors'
import { Badge, Button, EmptyState } from '../../components/primitives'
import { IconBell, IconCheck } from '../../components/Icons'
import { useAppStore } from '../../store/app-store'
import { describeError, useTranslation } from '../../i18n'
import { formatDateTime } from '../../lib/format'
import { FactsWindow } from './FactsWindow'

type TabKey = 'notifications' | 'facts'

/**
 * FR-HIS-1：通知里存的是错误码字符串（可能是数字业务码如 `40101`，
 * 也可能是 WS 错误码如 `AI_TIMEOUT`），两种形状都要能查到文案（`errorI18nKey`）。
 */
function notificationErrorText(code: string): string {
  const value = (code ?? '').trim()
  if (!value) return ''
  const numeric = /^\d+$/.test(value) ? Number(value) : null
  const key = errorI18nKey(numeric === null ? value : numeric)
  // 后端新增的未知错误码：原样展示，至少可检索（describeError 只会给通用兜底）
  if (key === 'error.unknown') return value
  return numeric === null ? describeError({ appErrorCode: value }) : describeError({ code: numeric })
}

export function HistoryPage(): JSX.Element {
  const { t } = useTranslation()

  const unread = useAppStore((s) => s.unreadNotifications)
  const pushToast = useAppStore((s) => s.pushToast)

  const [tab, setTab] = useState<TabKey>('notifications')
  const [notifications, setNotifications] = useState<BotNotification[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  /** 重新拉取列表，并用「未读条数」回写 store（FR-HIS-1 顶部未读数）。 */
  const load = useCallback(async (): Promise<void> => {
    try {
      const list = await window.tks.history.listNotifications(200)
      const sorted = [...list].sort((a, b) => b.timestamp - a.timestamp)
      setNotifications(sorted)
      useAppStore.getState().setUnread(sorted.filter((item) => !item.isRead).length)
      setError(null)
    } catch (err) {
      setError(describeError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
    const off = window.tks.history.onNotificationsUpdated(() => {
      void load()
    })
    return off
  }, [load])

  async function markRead(notificationId: string): Promise<void> {
    setBusy(true)
    try {
      await window.tks.history.markRead([notificationId])
      await load()
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setBusy(false)
    }
  }

  async function markAllRead(): Promise<void> {
    setBusy(true)
    try {
      await window.tks.history.markAllRead()
      await load()
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('history.title')}</h1>
        <div className="tab-bar" role="tablist" aria-label={t('history.title')}>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'notifications'}
            className={['tab', tab === 'notifications' ? 'is-active' : ''].filter(Boolean).join(' ')}
            onClick={() => setTab('notifications')}
          >
            {t('history.tab.notifications')}
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'facts'}
            className={['tab', tab === 'facts' ? 'is-active' : ''].filter(Boolean).join(' ')}
            onClick={() => setTab('facts')}
          >
            {t('history.tab.facts')}
          </button>
        </div>
      </header>

      <div className="page-body">
        {tab === 'notifications' ? (
          /* ------------------------- FR-HIS-1 Bot 通知 ------------------------- */
          <div className="section">
            <div className="row">
              <span className="row-label">
                <IconBell size={16} /> {t('history.notifications.unread', { n: unread })}
              </span>
              <div className="row-control">
                <Button
                  size="sm"
                  icon={<IconCheck size={16} />}
                  disabled={busy || unread === 0}
                  onClick={() => void markAllRead()}
                >
                  {t('history.notifications.markAllRead')}
                </Button>
              </div>
            </div>

            {error ? (
              <p className="field-error" role="alert">
                {error}
              </p>
            ) : null}

            {loading ? (
              <p className="muted">{t('common.loading')}</p>
            ) : notifications.length === 0 ? (
              <EmptyState icon={<IconBell size={28} />} title={t('history.notifications.empty')} />
            ) : (
              <div className="list">
                {notifications.map((item) => {
                  const label = notificationErrorText(item.errorCode)
                  return (
                    <div
                      className={['list-row', item.isRead ? '' : 'is-unread'].filter(Boolean).join(' ')}
                      key={item.notificationId}
                    >
                      <div className="list-main">
                        <span className="list-title">{item.message}</span>
                        <span className="list-sub">
                          {label ? <Badge tone={item.isRead ? 'neutral' : 'warn'}>{label}</Badge> : null}
                          {' '}
                          {formatDateTime(item.timestamp)}
                        </span>
                      </div>
                      <div className="list-actions">
                        {item.isRead ? null : (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={busy}
                            onClick={() => void markRead(item.notificationId)}
                          >
                            {t('history.notifications.markRead')}
                          </Button>
                        )}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        ) : (
          /* ---------------------- FR-HIS-2/3/4 记忆档案（只读） ---------------------- */
          <FactsWindow />
        )}
      </div>
    </div>
  )
}

export default HistoryPage
