/**
 * 已排程提醒（FR-REM-6）。
 *
 * 列表数据来自 `useAppStore.reminders`（主进程已从 SQLite 装载，FR-REM-4），
 * 本页只做展示 + 取消；「新建提醒」走 `reminders:create`（FR-REM-2 的目标时刻计算在主进程）。
 */

import { useEffect, useState } from 'react'
import type { ReminderRecord } from '@shared/protocol'
import { Button, EmptyState, Input } from '../../components/primitives'
import { IconAlarm, IconPlus, IconTrash } from '../../components/Icons'
import { useAppStore } from '../../store/app-store'
import { describeError, useTranslation } from '../../i18n'
import { formatDateTime } from '../../lib/format'

export function RemindersPage(): JSX.Element {
  const { t } = useTranslation()

  const reminders = useAppStore((s) => s.reminders)
  const setReminders = useAppStore((s) => s.setReminders)
  const pushToast = useAppStore((s) => s.pushToast)

  const [target, setTarget] = useState('')
  const [text, setText] = useState('')
  const [creating, setCreating] = useState(false)
  const [cancellingId, setCancellingId] = useState<string | null>(null)

  // 进入页面时同步一次权威列表，并订阅主进程变更（触发/取消/休眠补发）
  useEffect(() => {
    let alive = true
    void window.tks.reminders
      .list()
      .then((list) => {
        if (alive) setReminders(list)
      })
      .catch(() => {
        /* 静默：列表仍可从 store 展示 */
      })
    const off = window.tks.reminders.onChanged((event) => setReminders(event.reminders))
    return () => {
      alive = false
      off()
    }
  }, [setReminders])

  /** FR-REM-6：展示进行中的提醒（已触发/已取消由主进程过滤，这里再兜底一次）。 */
  const pending: ReminderRecord[] = reminders.filter((item) => item.status === 'pending')

  async function cancel(item: ReminderRecord): Promise<void> {
    setCancellingId(item.reminderId)
    try {
      await window.tks.reminders.cancel(item.reminderId)
      pushToast({ level: 'success', i18nKey: 'reminder.cancelled' })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setCancellingId(null)
    }
  }

  async function create(): Promise<void> {
    if (!target || !text.trim()) {
      pushToast({ level: 'warn', i18nKey: 'reminder.error.empty' })
      return
    }
    setCreating(true)
    try {
      const created = await window.tks.reminders.create({ target, text: text.trim() })
      pushToast({ level: 'success', i18nKey: 'reminder.created', params: { time: created.targetTime } })
      setTarget('')
      setText('')
      setReminders(await window.tks.reminders.list())
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setCreating(false)
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('reminder.title')}</h1>
      </header>

      <div className="page-body">
        <section className="section">
          <h2 className="section-title">{t('reminder.create')}</h2>
          <div className="row">
            <div className="row-control">
              <Input
                label={t('reminder.time')}
                type="time"
                value={target}
                onChange={(e) => setTarget(e.target.value)}
              />
              <Input
                label={t('reminder.text')}
                type="text"
                value={text}
                onChange={(e) => setText(e.target.value)}
              />
              <Button variant="primary" icon={<IconPlus size={16} />} loading={creating} onClick={() => void create()}>
                {t('reminder.create')}
              </Button>
            </div>
          </div>
        </section>

        <section className="section">
          <h2 className="section-title">{t('reminder.title')}</h2>
          {pending.length === 0 ? (
            <EmptyState icon={<IconAlarm size={28} />} title={t('reminder.empty')} />
          ) : (
            <div className="list">
              {pending.map((item) => (
                <div className="list-row reminder-row" key={item.reminderId}>
                  <div className="list-main">
                    <span className="list-title">{item.text}</span>
                    <span className="list-sub">
                      {item.targetTime} · {formatDateTime(item.fireAt)}
                    </span>
                  </div>
                  <div className="list-actions">
                    <Button
                      variant="ghost"
                      size="sm"
                      icon={<IconTrash size={16} />}
                      loading={cancellingId === item.reminderId}
                      onClick={() => void cancel(item)}
                    >
                      {t('reminder.cancel')}
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </div>
  )
}

export default RemindersPage
