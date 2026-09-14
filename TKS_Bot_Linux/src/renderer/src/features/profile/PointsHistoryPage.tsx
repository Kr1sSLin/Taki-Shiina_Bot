/**
 * 积分流水页（FR-PT-2 / FR-PT-3 / FR-PT-4 / FR-PT-7）。
 *
 * 关键约束：
 *  - FR-PT-2：分页拉取 `GET /points/history?page&pageSize&reasonCode`；支持按事由过滤。
 *  - FR-PT-3：事由按 `reason_code` 本地化（`ITEM_SEND` 带 `related_item_id` 作为物品名）。
 *  - FR-PT-4：服务端同日触发的多条规则**各自独立成行**，本页不做任何合并聚合。
 *  - FR-PT-7：金额以 `+N` / `-N` 展示并按正负着色；`balance_after` 作为「变动后余额」列。
 *  - FR-PROG-2：不实现任何积分规则，只渲染服务端流水。
 *
 * ⚠️ 字段名契约（§7.0）：`/points/history` 的 `items[]` 是 **snake_case**
 *    （`change_amount` / `reason_code` / `balance_after` / `related_item_id` / `created_at`）。
 */

import { useEffect } from 'react'
import { Button, EmptyState, Spinner } from '../../components/primitives'
import { IconHistory } from '../../components/Icons'
import { t as translate, useTranslation } from '../../i18n'
import { formatDateTime } from '../../lib/format'
import { useProfileStore } from '../../store/profile-store'

/** 过滤选项：服务端当前会下发的 `reason_code`（未知码在 UI 上仍可正常展示为「积分变动」）。 */
const REASON_FILTERS: string[] = [
  'DAILY_FIRST_CHAT',
  'STREAK_3_DAY',
  'ANNIVERSARY',
  'ITEM_SEND',
  'ITEM_REFUND',
  'ADMIN_ADJUST'
]

const FILTER_ALL = 'ALL'

/** FR-PT-3：`reason_code` → 本地化事由。未知码一律回退「积分变动」。 */
function reasonLabel(reasonCode: string, relatedItemId: string | null): string {
  switch (reasonCode) {
    case 'DAILY_FIRST_CHAT':
      return translate('points.reason.DAILY_FIRST_CHAT')
    case 'STREAK_3_DAY':
      return translate('points.reason.STREAK_3_DAY')
    case 'ANNIVERSARY':
      return translate('points.reason.ANNIVERSARY')
    case 'ITEM_SEND':
      // 服务端不下发物品名，只有 `related_item_id`；缺失时退回通用文案
      return relatedItemId
        ? translate('points.reason.ITEM_SEND', { item: relatedItemId })
        : translate('points.reason.ITEM_SEND.generic')
    case 'ITEM_REFUND':
      return translate('points.reason.ITEM_REFUND')
    case 'ADMIN_ADJUST':
      return translate('points.reason.ADMIN_ADJUST')
    default:
      return translate('points.reason.UNKNOWN')
  }
}

/** FR-PT-7：`+N` / `-N`（0 归入正向展示）。 */
function amountText(amount: number): string {
  return `${amount >= 0 ? '+' : '-'}${Math.abs(amount)}`
}

export function PointsHistoryPage(): JSX.Element {
  const { t } = useTranslation()

  const ledger = useProfileStore((s) => s.ledger)
  const ledgerHasMore = useProfileStore((s) => s.ledgerHasMore)
  const ledgerLoading = useProfileStore((s) => s.ledgerLoading)
  const ledgerTotal = useProfileStore((s) => s.ledgerTotal)
  const ledgerReasonFilter = useProfileStore((s) => s.ledgerReasonFilter)
  const loadLedger = useProfileStore((s) => s.loadLedger)

  // FR-PT-2：进入页面重置到第 1 页
  useEffect(() => {
    void loadLedger({ reset: true })
  }, [loadLedger])

  const activeFilter = ledgerReasonFilter ?? FILTER_ALL
  const showInitialLoading = ledgerLoading && ledger.length === 0

  const selectFilter = (code: string): void => {
    // 过滤条件变化即重置分页；store 会记住当前 filter 供「加载更多」复用
    void loadLedger({ reset: true, reasonCode: code === FILTER_ALL ? null : code })
  }

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('profile.ledger')}</h1>
        <p className="muted">{t('points.ledger.total', { n: ledgerTotal })}</p>
      </header>

      <div className="page-body">
        <div className="filter-row" role="group" aria-label={t('profile.ledger.filter.reason')}>
          <button
            type="button"
            className={['chip', activeFilter === FILTER_ALL ? 'is-active' : ''].filter(Boolean).join(' ')}
            aria-pressed={activeFilter === FILTER_ALL}
            onClick={() => selectFilter(FILTER_ALL)}
          >
            {t('common.all')}
          </button>
          {REASON_FILTERS.map((code) => (
            <button
              key={code}
              type="button"
              className={['chip', activeFilter === code ? 'is-active' : ''].filter(Boolean).join(' ')}
              aria-pressed={activeFilter === code}
              onClick={() => selectFilter(code)}
            >
              {reasonLabel(code, null)}
            </button>
          ))}
        </div>

        {showInitialLoading ? (
          <div className="empty-state">
            <Spinner size={24} />
            <p className="empty-title">{t('common.loading')}</p>
          </div>
        ) : ledger.length === 0 ? (
          <EmptyState icon={<IconHistory size={28} />} title={t('points.ledger.empty')} />
        ) : (
          <>
            {/* FR-PT-4：每条规则变动独立成行 */}
            <ul className="ledger-list">
              {ledger.map((entry) => {
                const positive = entry.change_amount >= 0
                return (
                  <li className="ledger-row" key={entry.id}>
                    <span className="ledger-meta">{formatDateTime(entry.created_at)}</span>
                    <span className="ledger-reason">{reasonLabel(entry.reason_code, entry.related_item_id)}</span>
                    <span className={['ledger-amount', positive ? 'is-plus' : 'is-minus'].join(' ')}>
                      {amountText(entry.change_amount)}
                    </span>
                    <span className="ledger-meta">
                      {t('points.ledger.balanceAfter', { n: entry.balance_after })}
                    </span>
                  </li>
                )
              })}
            </ul>

            {ledgerHasMore ? (
              <Button variant="secondary" block loading={ledgerLoading} onClick={() => void loadLedger({})}>
                {ledgerLoading ? t('common.loading') : t('points.ledger.loadMore')}
              </Button>
            ) : (
              <p className="muted">{t('points.ledger.noMore')}</p>
            )}
          </>
        )}
      </div>
    </div>
  )
}

export default PointsHistoryPage
