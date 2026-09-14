/**
 * Profile 页 —— 积分 / 等级 / 补签卡首页。
 *
 * 关键约束（PRD §7.2 / §7.3 / §7.4 / §7.5）：
 *  - FR-PT-1 / FR-PROG-5：首屏一次 `GET /points/overview` 拿全量（`store.refreshAll` 内部聚合），不轮询。
 *  - FR-PROG-2：**不在渲染端实现任何积分/等级规则**，本页只展示服务端数据。
 *  - FR-PROG-3：日期一律使用服务端返回的字符串（`formatServerDate`），不用本地当天日期推算。
 *  - FR-PROG-4：离线时回显缓存并标注「离线数据，最后更新于 {时间}」。
 *  - FR-LV-1：升级进度用 `LevelProgressBar`，比例仅作展示。
 *  - FR-LV-3：等级为默认态时使用中性兜底文案，绝不渲染空标题/空徽章。
 *  - FR-LV-8：断签导致等级清零时明确「积分余额不受影响」。
 *  - FR-LV-9：展示 `breakDeadlineDate` 与 `gapDays`。
 *  - FR-MC-1：补签卡规则（每月发放 / 上限）全部取服务端字段，不硬编码 1 / 12。
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import type { LevelStatus, UserProgressCache } from '@shared/protocol'
import { Badge, Button, EmptyState, Spinner } from '../../components/primitives'
import { IconCalendar, IconHistory, IconInfo, IconWarning } from '../../components/Icons'
import { LevelBadge, LevelProgressBar } from '../../components/LevelBadge'
import { useReducedMotion } from '../../components/visual'
import { useTranslation } from '../../i18n'
import { daysBetweenDateStrings, formatDateTime, formatServerDate } from '../../lib/format'
import { levelProgress, normalizeLevels, resolveLevelFromStatus } from '../../lib/level'
import { useProfileStore } from '../../store/profile-store'
import { PointsBalanceCard } from './PointsBalanceCard'

/**
 * FR-PROG-4 离线兜底：把本地缓存的进度快照映射为展示用的 `LevelStatus`。
 *
 * 纯字段搬运（缓存内容本身就是服务端快照），不参与任何业务判定（FR-PROG-2）。
 */
function statusFromCache(cache: UserProgressCache): LevelStatus {
  return {
    levelCode: cache.levelCode,
    levelName: cache.levelName,
    prevLevelCode: cache.prevLevelCode,
    continuousDays: cache.continuousDays,
    changeType: null,
    changeSource: null,
    highestLevelCode: cache.highestLevelCode,
    lastValidDate: cache.lastValidDate,
    gapDays: cache.gapDays,
    breakDeadlineDate: cache.breakDeadlineDate,
    nextLevelCode: cache.nextLevelCode,
    nextLevelName: cache.nextLevelName,
    nextLevelThresholdDays: cache.nextLevelThresholdDays,
    daysToNextLevel: cache.daysToNextLevel,
    levelUpdatedAt: cache.levelUpdatedAt,
    isDefaultLevel: cache.isDefaultLevel,
    availableMakeupCards: cache.availableMakeupCards
  }
}

export function ProfilePage(): JSX.Element {
  const { t } = useTranslation()
  const reducedMotion = useReducedMotion()

  const loaded = useProfileStore((s) => s.loaded)
  const offline = useProfileStore((s) => s.offline)
  const overview = useProfileStore((s) => s.overview)
  const cached = useProfileStore((s) => s.cached)
  const levelConfig = useProfileStore((s) => s.levelConfig)
  const makeupCard = useProfileStore((s) => s.makeupCard)
  const balance = useProfileStore((s) => s.balance)
  const refreshAll = useProfileStore((s) => s.refreshAll)

  const [refreshing, setRefreshing] = useState(false)

  // FR-PT-1：首屏 1 次请求拿全量（refreshAll 内部走 /points/overview 聚合）
  useEffect(() => {
    void refreshAll()
  }, [refreshAll])

  const handleRefresh = useCallback((): void => {
    setRefreshing(true)
    void refreshAll().finally(() => setRefreshing(false))
  }, [refreshAll])

  // FR-LV-2：等级定义以服务端 /level/config 为准（本地常量仅兜底配色）
  const levelEntries = useMemo(() => normalizeLevels(levelConfig), [levelConfig])

  // 优先实时数据；离线时退回服务端快照缓存（FR-PROG-4）
  const status = useMemo(
    () => overview?.level ?? (cached ? statusFromCache(cached) : null),
    [overview, cached]
  )

  const level = useMemo(() => resolveLevelFromStatus(status, levelEntries), [status, levelEntries])
  const progress = useMemo(() => levelProgress(status, levelEntries), [status, levelEntries])

  // FR-LV-3：服务端在 levelCode=NONE 时下发空 levelName，这里绝不允许渲染空标题
  const levelName = level.isDefault ? t('profile.level.none') : level.name.trim() || t('profile.level.none')

  const continuousDays = status?.continuousDays ?? 0
  const gapDays = status?.gapDays ?? 0
  const breakDeadlineDate = status?.breakDeadlineDate ?? null
  const showResetNotice = status?.changeType === 'RESET' || status?.levelCode === 'NONE'

  /**
   * 断签预警剩余天数：**只使用服务端字段**（`lastValidDate` / `breakDeadlineDate` / `gapDays`）
   * 做展示用减法，不引入本地当天日期（FR-PROG-3）。
   */
  const remainingDays = useMemo(() => {
    const lastValid = status?.lastValidDate ?? null
    const deadline = status?.breakDeadlineDate ?? null
    if (!lastValid || !deadline) return null
    const allowedGap = daysBetweenDateStrings(lastValid, deadline)
    if (allowedGap === null) return null
    return Math.max(0, allowedGap - (status?.gapDays ?? 0))
  }, [status])

  const makeup = overview?.makeupCard ?? makeupCard
  const makeupAvailable = makeup?.available ?? status?.availableMakeupCards ?? 0

  const offlineText = t('profile.offline', {
    time: cached ? formatDateTime(cached.syncedAt) : t('common.unknown')
  })

  if (!loaded) {
    return (
      <div className="page">
        <header className="page-head">
          <h1 className="page-title">{t('profile.title')}</h1>
        </header>
        <div className="page-body">
          <div className="empty-state">
            <Spinner size={24} />
            <p className="empty-title">{t('common.loading')}</p>
          </div>
        </div>
      </div>
    )
  }

  // 既无实时数据也无缓存 → 明确报错 + 重试
  if (!overview && !cached) {
    return (
      <div className="page">
        <header className="page-head">
          <h1 className="page-title">{t('profile.title')}</h1>
        </header>
        <div className="page-body">
          <EmptyState icon={<IconWarning size={28} />} title={t('profile.error.load')} />
          <Button variant="primary" block loading={refreshing} onClick={handleRefresh}>
            {t('common.retry')}
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('profile.title')}</h1>
      </header>

      <div className="page-body">
        {offline ? (
          <div className="offline-banner" role="status">
            <IconWarning size={16} />
            <span>{offlineText}</span>
          </div>
        ) : null}

        <div className="profile-grid">
          <PointsBalanceCard balance={balance} refreshing={refreshing} onRefresh={handleRefresh} />

          <section className="level-card glass" aria-label={t('profile.level')}>
            <div className="level-head">
              <LevelBadge level={level} size={72} />
              <div>
                <p className="stat-label">{t('profile.level')}</p>
                <h2 className="card-title">{levelName}</h2>
              </div>
            </div>

            <div className="band">
              <h3 className="band-title">{t('profile.progress')}</h3>
              {progress.isMax ? (
                // 已是最高等级：没有下一档阈值，服务端不下发 nextLevelThresholdDays
                <p className="muted">{t('profile.progress.max')}</p>
              ) : (
                <>
                  <LevelProgressBar
                    ratio={progress.ratio}
                    accent={level.accent}
                    // NFR-11：系统「减少动画」时不做七彩流动效果
                    animated={level.animated && !reducedMotion}
                    label={t('profile.progress')}
                  />
                  {progress.nextName !== null && progress.daysToNext !== null ? (
                    <p className="muted">
                      {t('profile.progress.detail', {
                        next: progress.nextName,
                        days: progress.daysToNext
                      })}
                    </p>
                  ) : null}
                  <p className="muted">
                    {t('profile.progress.threshold', {
                      current: progress.currentDays,
                      target: progress.targetDays ?? 0
                    })}
                  </p>
                </>
              )}
            </div>

            {/* FR-LV-9：连续天数 / 断签缺口 / 断签截止日 */}
            <div className="band">
              <h3 className="band-title">{t('profile.continuousDays')}</h3>
              <div className="streak-row">
                <span className="stat-value">{t('profile.days', { n: continuousDays })}</span>
                {gapDays > 0 ? <Badge tone="warn">{t('profile.gapDays', { n: gapDays })}</Badge> : null}
                {breakDeadlineDate ? (
                  <span className="muted">
                    {t('profile.breakDeadline', { date: formatServerDate(breakDeadlineDate) })}
                  </span>
                ) : null}
                {gapDays > 0 && remainingDays !== null ? (
                  <Badge tone="error">{t('profile.breakPending', { n: remainingDays })}</Badge>
                ) : null}
              </div>
            </div>

            {showResetNotice ? (
              // FR-LV-8：断签清零只影响等级，必须明说积分不受影响
              <div className="band">
                <div className="streak-row">
                  <IconInfo size={16} />
                  <span>{t('profile.resetNotice')}</span>
                </div>
              </div>
            ) : null}
          </section>

          <section className="band" aria-label={t('profile.makeupCards')}>
            <h3 className="band-title">{t('profile.makeupCards')}</h3>
            <div className="stat-grid">
              <div className="stat-item">
                <span className="stat-label">{t('profile.makeupCards')}</span>
                <span className="stat-value">{t('profile.makeupCards.available', { n: makeupAvailable })}</span>
              </div>
            </div>
            {/* FR-MC-1：每月发放张数与上限一律取服务端字段；服务端不可用时不猜、不渲染规则文案 */}
            {makeup ? (
              <p className="muted">
                {t('profile.makeupCards.rules', {
                  monthly: makeup.monthlyGrant,
                  max: makeup.maxAvailable
                })}
              </p>
            ) : null}
            {/* FR-MC-4：补签入口 */}
            <Link className="btn btn-primary btn-block" to="/profile/makeup">
              <IconCalendar size={16} />
              <span>{t('profile.makeupCards.goCalendar')}</span>
            </Link>
          </section>
        </div>

        <nav className="band" aria-label={t('nav.profile')}>
          <div className="streak-row">
            <Link className="btn btn-secondary" to="/profile/ledger">
              <IconHistory size={16} />
              <span>{t('profile.ledger')}</span>
            </Link>
            <Link className="btn btn-secondary" to="/profile/levels">
              <IconInfo size={16} />
              <span>{t('profile.levelGuide')}</span>
            </Link>
          </div>
        </nav>
      </div>
    </div>
  )
}

export default ProfilePage
