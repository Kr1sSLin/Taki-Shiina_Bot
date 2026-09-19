/**
 * 积分余额卡片（FR-PT-1）。
 *
 * 可复用的小卡片：展示**服务端下发**的原始余额数字 + 一个刷新按钮。
 *
 * 关键约束：
 *  - FR-PROG-2：余额只展示，不在渲染端做任何加减推算。
 *  - NFR-11：刷新按钮为纯图标按钮，必须有 `aria-label`；加载态用 `aria-busy` 标注。
 */

import { useTranslation } from '../../i18n'
import { IconButton, Spinner } from '../../components/primitives'
import { IconRefresh } from '../../components/Icons'

export interface PointsBalanceCardProps {
  /** 服务端余额原值（`PointsOverviewData.balance` / `points.balance`）。 */
  balance: number
  /** 正在刷新时禁用按钮并展示 spinner。 */
  refreshing?: boolean
  onRefresh: () => void
}

export function PointsBalanceCard({ balance, refreshing = false, onRefresh }: PointsBalanceCardProps): JSX.Element {
  const { t } = useTranslation()

  return (
    <section className="card glass balance-card" aria-label={t('profile.balance')}>
      <header className="card-head">
        <h3 className="card-title">{t('profile.balance')}</h3>
        <IconButton
          label={refreshing ? t('common.loading') : t('common.refresh')}
          onClick={onRefresh}
          disabled={refreshing}
          aria-busy={refreshing || undefined}
        >
          {refreshing ? <Spinner size={16} /> : <IconRefresh size={18} />}
        </IconButton>
      </header>
      {/* 与 `.card-head` 同用 `.card-body` 的内边距，余额数字才和标题左对齐 */}
      <div className="card-body">
        <p className="balance-value">{balance}</p>
      </div>
    </section>
  )
}

export default PointsBalanceCard
