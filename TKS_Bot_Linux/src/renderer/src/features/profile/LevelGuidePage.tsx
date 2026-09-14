/**
 * 等级说明页（FR-LV-2）。
 *
 * 关键约束：
 *  - FR-LV-2：等级视觉与阈值**由 `GET /level/config` 动态驱动**，服务端不可用时
 *    `buildLevelGuide` 自动回退到与 `视觉资产/熊猫图像.txt` 一致的设计兜底值。
 *  - FR-PROG-2：本页只做展示，不参与任何等级判定。
 *
 * 说明：store 的 `levels` 已是「解析后的 `ResolvedLevel[]`」（供徽章渲染），
 * 而 `buildLevelGuide` 需要服务端 `levels[]`（snake_case）条目——两者同源于 `/level/config`，
 * 因此这里从 `levelConfig` 规范化后构建等级列表。
 */

import { useEffect, useMemo } from 'react'
import { Badge } from '../../components/primitives'
import { LevelBadge } from '../../components/LevelBadge'
import { useTranslation } from '../../i18n'
import { buildLevelGuide, normalizeLevels } from '../../lib/level'
import { useProfileStore } from '../../store/profile-store'

export function LevelGuidePage(): JSX.Element {
  const { t } = useTranslation()

  const levelConfig = useProfileStore((s) => s.levelConfig)
  const overview = useProfileStore((s) => s.overview)
  const refreshLevelConfig = useProfileStore((s) => s.refreshLevelConfig)

  // FR-LV-2：阈值可能被运营后台调整，每次进入页面重新拉取
  useEffect(() => {
    void refreshLevelConfig()
  }, [refreshLevelConfig])

  const entries = useMemo(() => normalizeLevels(levelConfig), [levelConfig])
  const guide = useMemo(() => buildLevelGuide(entries), [entries])

  const currentCode = overview && !overview.level.isDefaultLevel ? overview.level.levelCode : null

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('profile.levelGuide')}</h1>
        <p className="muted">{t('points.levelGuide.serverHint')}</p>
      </header>

      <div className="page-body">
        <div className="level-guide-list">
          {guide.map((item) => (
            <div className="level-guide-row" key={item.code}>
              <LevelBadge level={item} size={48} />
              {/* name 永不为空：服务端为空时 resolveLevel 已用中性兜底（FR-LV-3） */}
              <span>{item.name}</span>
              {item.code === currentCode ? <Badge tone="accent">{t('points.levelGuide.current')}</Badge> : null}
              <span className="level-guide-threshold">
                {t('profile.levelGuide.threshold', { n: item.thresholdDays ?? 0 })}
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}

export default LevelGuidePage
