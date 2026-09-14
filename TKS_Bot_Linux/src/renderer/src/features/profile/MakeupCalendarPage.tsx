/**
 * 补签日历（FR-MC-2 ~ FR-MC-8）。
 *
 * 关键约束：
 *  - FR-MC-2：日历数据源**只能是** `useProfileStore.candidates`
 *    （即 `GET /points/makeup-card/candidates?limit=120` 的 `items[{date, daysAgo}]`）。
 *    **绝不用本地日期推算缺口**（违反 FR-PROG-3）——本页只按服务端日期字符串的 `YYYY-MM`
 *    前缀做**展示用**分组，不构造任何日期。
 *  - FR-MC-3：补签必须用户主动点击 + 二次确认「将消耗 1 张补签卡补签 {date}」，绝不自动使用。
 *  - FR-MC-5：按返回的 `level.changeType` 决定反馈强度 —— `RESTORE` 克制（仅轻量 toast，
 *    **不弹庆祝**，庆祝动画由主进程按 UPGRADE 事件驱动），`UPGRADE` 才走升级庆祝。
 *  - FR-MC-6 / EDGE-L19：40205 / 40206 / 40207 给出明确文案，并刷新日历与余额；
 *    并发冲突交给服务端唯一约束，客户端不做本地并发控制。
 *  - FR-MC-7：展示 `GET /points/makeup-card/history` 的 snake_case 记录。
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import type { MakeupCandidate, MakeupCardRecord } from '@shared/protocol'
import { Badge, Button, EmptyState, Modal, Spinner } from '../../components/primitives'
import { IconCalendar } from '../../components/Icons'
import { describeError, useTranslation, type TFunction } from '../../i18n'
import { formatDateTime, formatServerDate } from '../../lib/format'
import { useAppStore } from '../../store/app-store'
import { useProfileStore } from '../../store/profile-store'

const HISTORY_PAGE_SIZE = 20
/** `date` 的 `YYYY-MM-DD` 前缀（仅用于展示分组，见 FR-PROG-3 说明）。 */
const DATE_PREFIX = /^(\d{4})-(\d{2})-\d{2}$/

interface MonthGroup {
  /** `YYYY-MM`，用作 React key 与月份标题数据源。 */
  month: string
  year: string
  /** 去掉前导零的月份，如 `09` → `9`。 */
  monthNumber: string
  items: MakeupCandidate[]
}

/** 按服务端日期字符串分组（不重算日期）。 */
function groupByMonth(items: MakeupCandidate[]): MonthGroup[] {
  const groups: MonthGroup[] = []
  const index = new Map<string, number>()
  const seen = new Set<string>()

  for (const item of items) {
    const date = typeof item?.date === 'string' ? item.date : ''
    const matched = DATE_PREFIX.exec(date)
    if (!matched) continue
    if (seen.has(date)) continue
    seen.add(date)

    const month = `${matched[1]}-${matched[2]}`
    const existing = index.get(month)
    if (existing === undefined) {
      index.set(month, groups.length)
      groups.push({
        month,
        year: matched[1],
        monthNumber: String(Number(matched[2])),
        items: [item]
      })
    } else {
      groups[existing].items.push(item)
    }
  }

  return groups
}

/** FR-MC-7：卡片状态本地化（未知状态不泄漏 i18n key）。 */
function statusLabel(t: TFunction, status: string): string {
  if (status === 'AVAILABLE') return t('makeup.status.AVAILABLE')
  if (status === 'USED') return t('makeup.status.USED')
  return t('common.unknown')
}

/** FR-MC-7：发放记录看 `granted_month`，使用记录看 `used_for_date`。 */
function historyLabel(t: TFunction, record: MakeupCardRecord): string {
  if (record.granted_month) return t('makeup.history.granted', { month: record.granted_month })
  if (record.used_for_date) return t('makeup.history.used', { date: formatServerDate(record.used_for_date) })
  return t('common.unknown')
}

export function MakeupCalendarPage(): JSX.Element {
  const { t } = useTranslation()

  const candidates = useProfileStore((s) => s.candidates)
  const refreshMakeup = useProfileStore((s) => s.refreshMakeup)
  const refreshOverview = useProfileStore((s) => s.refreshOverview)
  const pushToast = useAppStore((s) => s.pushToast)

  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [history, setHistory] = useState<MakeupCardRecord[] | null>(null)

  /** FR-MC-7：补签卡发放/使用记录（snake_case 字段）。 */
  const loadHistory = useCallback(async (): Promise<void> => {
    try {
      const data = await window.tks.gamification.makeupHistory({ page: 1, pageSize: HISTORY_PAGE_SIZE })
      setHistory(data.items)
    } catch {
      setHistory([])
    }
  }, [])

  useEffect(() => {
    void loadHistory()
  }, [loadHistory])

  // 直接进本页（未经过 Profile）时，store 里可能还没有候选日期 → 主动拉一次
  useEffect(() => {
    if (!candidates) void refreshMakeup()
  }, [candidates, refreshMakeup])

  const groups = useMemo(() => groupByMonth(candidates?.items ?? []), [candidates])

  /** 已在补签记录里出现过的日期（服务端数据推导，仅用于把该格置灰）。 */
  const usedDates = useMemo(() => {
    const dates = new Set<string>()
    for (const record of history ?? []) {
      if (record.used_for_date) dates.add(record.used_for_date)
    }
    return dates
  }, [history])

  const available = candidates?.available ?? 0

  const closeConfirm = useCallback((): void => {
    if (!submitting) setSelectedDate(null)
  }, [submitting])

  const handleConfirm = useCallback(async (): Promise<void> => {
    const targetDate = selectedDate
    if (!targetDate || submitting) return

    setSubmitting(true)
    try {
      const result = await window.tks.gamification.makeupUse(targetDate)

      if (result.ok) {
        const changeType = result.data?.level?.changeType ?? null
        const levelName = (result.data?.level?.levelName ?? '').trim() || t('profile.level.none')

        if (changeType === 'RESTORE') {
          // FR-LV-5 / FR-MC-5：等级恢复刻意克制——只给轻量提示，不弹庆祝动画
          pushToast({ level: 'info', i18nKey: 'makeup.success.restore', params: { level: levelName } })
        } else if (changeType === 'UPGRADE') {
          // FR-LV-4：升级庆祝由主进程按 changeType 推送事件驱动，这里只补一条 toast
          pushToast({ level: 'success', i18nKey: 'makeup.success.upgrade', params: { level: levelName } })
        } else {
          pushToast({ level: 'success', i18nKey: 'makeup.success', params: { date: formatServerDate(targetDate) } })
        }
        setSelectedDate(null)
      } else {
        // FR-MC-6：明确文案 + 刷新日历（失败不消耗卡片）
        const i18nKey =
          result.code === 40205
            ? 'error.api.40205'
            : result.code === 40206
              ? 'error.api.40206'
              : result.code === 40207
                ? 'error.api.40207'
                : 'error.unknown'
        pushToast({ level: 'error', i18nKey })
        setSelectedDate(null)
      }
    } catch (err) {
      // `text` 优先于 `i18nKey` 展示；i18nKey 仅作兜底
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
      setSelectedDate(null)
    } finally {
      // FR-MC-6 / FR-MC-8 / EDGE-L19：无论成败都刷新日历、余额与记录
      await Promise.all([refreshMakeup(), refreshOverview(), loadHistory()])
      setSubmitting(false)
    }
  }, [selectedDate, submitting, pushToast, refreshMakeup, refreshOverview, loadHistory, t])

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('makeup.title')}</h1>
        <p className="muted">{t('makeup.subtitle')}</p>
      </header>

      <div className="page-body">
        <div className="band">
          <div className="streak-row">
            <Badge tone="accent">{t('makeup.available', { n: available })}</Badge>
          </div>
        </div>

        {groups.length === 0 ? (
          // FR-MC-2：没有服务端候选日期就没有可补签的格子
          <EmptyState icon={<IconCalendar size={28} />} title={t('makeup.empty')} />
        ) : (
          <div className="makeup-grid">
            {groups.map((group) => (
              <section
                key={group.month}
                className="makeup-month"
                aria-label={t('points.makeup.monthTitle', { year: group.year, month: group.monthNumber })}
              >
                <h3 className="makeup-month-title">
                  {t('points.makeup.monthTitle', { year: group.year, month: group.monthNumber })}
                </h3>
                <div className="makeup-days">
                  {group.items.map((item) => {
                    const alreadyUsed = usedDates.has(item.date)
                    return (
                      <button
                        key={item.date}
                        type="button"
                        className={['makeup-day', 'is-candidate', alreadyUsed ? 'is-used' : '']
                          .filter(Boolean)
                          .join(' ')}
                        aria-label={t('points.makeup.selectDate', { date: formatServerDate(item.date) })}
                        disabled={alreadyUsed || submitting}
                        onClick={() => setSelectedDate(item.date)}
                      >
                        <span>{formatServerDate(item.date)}</span>
                        <span className="ledger-meta">{t('common.daysAgo', { n: item.daysAgo })}</span>
                      </button>
                    )
                  })}
                </div>
              </section>
            ))}
          </div>
        )}

        <section className="band" aria-label={t('makeup.history')}>
          <h3 className="band-title">{t('makeup.history')}</h3>
          {history === null ? (
            <Spinner size={18} />
          ) : history.length === 0 ? (
            <EmptyState title={t('makeup.history.empty')} />
          ) : (
            <div className="makeup-history">
              {history.map((record) => (
                <div className="makeup-history-row" key={record.id}>
                  <span>{historyLabel(t, record)}</span>
                  <Badge tone={record.status === 'USED' ? 'neutral' : 'ok'}>{statusLabel(t, record.status)}</Badge>
                  <span className="ledger-meta">{formatDateTime(record.created_at)}</span>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>

      {/* FR-MC-3：二次确认，用户必须显式点击「使用补签卡」 */}
      <Modal
        open={selectedDate !== null}
        role="alertdialog"
        title={t('makeup.confirm.title')}
        onClose={closeConfirm}
        footer={
          <>
            <Button variant="ghost" onClick={closeConfirm} disabled={submitting}>
              {t('common.cancel')}
            </Button>
            <Button variant="primary" loading={submitting} onClick={() => void handleConfirm()}>
              {t('makeup.confirm.ok')}
            </Button>
          </>
        }
      >
        <p>
          {selectedDate
            ? t('makeup.confirm.body', { date: formatServerDate(selectedDate) })
            : ''}
        </p>
      </Modal>
    </div>
  )
}

export default MakeupCalendarPage
