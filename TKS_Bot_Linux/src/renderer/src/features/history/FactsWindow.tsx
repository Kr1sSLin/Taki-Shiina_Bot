/**
 * 记忆档案列表（FR-HIS-2 / FR-HIS-3 / FR-HIS-4）。
 *
 * 独立组件：既用于历史页的「记忆档案」Tab，也用于 FR-DSK-8 的独立记忆窗口（`HistoryWindow`）。
 *
 * ⚠️ FR-HIS-4：后端**没有**删除/编辑接口，这里只读——不得提供任何删除或编辑入口，
 *    以免造成「已删除但服务端仍保留」的错觉。
 */

import { useEffect, useMemo, useState } from 'react'
import type { UserFact } from '@shared/protocol'
import { EmptyState, Input } from '../../components/primitives'
import { IconInfo, IconSearch } from '../../components/Icons'
import { describeError, useTranslation } from '../../i18n'
import { formatDateTime } from '../../lib/format'

/** 本地日历日 `YYYY-MM-DD`，仅用于展示层日期筛选（不参与任何业务判定，EDGE-L13）。 */
function localDate(ms: number): string {
  const d = new Date(ms)
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

export function FactsWindow(): JSX.Element {
  const { t } = useTranslation()

  const [facts, setFacts] = useState<UserFact[]>([])
  const [keyword, setKeyword] = useState('')
  const [fromDate, setFromDate] = useState('')
  const [toDate, setToDate] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let alive = true

    const load = async (): Promise<void> => {
      try {
        const list = await window.tks.history.listFacts(300)
        if (!alive) return
        // FR-HIS-2：按时间倒序
        setFacts([...list].sort((a, b) => b.timestamp - a.timestamp))
        setError(null)
      } catch (err) {
        if (alive) setError(describeError(err))
      } finally {
        if (alive) setLoading(false)
      }
    }

    void load()

    // FR-SYNC-7：`memory.fact.created` 实时更新（主进程推送增量）
    const off = window.tks.history.onFactsUpdated((event) => {
      setFacts((current) => {
        const map = new Map(current.map((fact) => [fact.factId, fact]))
        event.facts.forEach((fact) => map.set(fact.factId, fact))
        return [...map.values()].sort((a, b) => b.timestamp - a.timestamp)
      })
    })

    return () => {
      alive = false
      off()
    }
  }, [])

  // FR-HIS-3：关键词（大小写不敏感） + 日期区间筛选，均为纯客户端过滤
  const filtered = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    return facts.filter((fact) => {
      if (needle && !fact.fact.toLowerCase().includes(needle)) return false
      const day = localDate(fact.timestamp)
      if (fromDate && day < fromDate) return false
      if (toDate && day > toDate) return false
      return true
    })
  }, [facts, keyword, fromDate, toDate])

  return (
    <div className="section">
      {/* FR-HIS-4：只读提示放在最显眼的位置 */}
      <p className="row-hint" role="note">
        <IconInfo size={16} /> {t('history.facts.readonly')}
      </p>

      <div className="search-row">
        <Input
          type="search"
          aria-label={t('history.facts.search')}
          placeholder={t('history.facts.search')}
          value={keyword}
          onChange={(e) => setKeyword(e.target.value)}
        />
      </div>

      <div className="filter-row">
        <Input
          label={t('history.facts.dateFrom')}
          type="date"
          value={fromDate}
          onChange={(e) => setFromDate(e.target.value)}
        />
        <Input
          label={t('history.facts.dateTo')}
          type="date"
          value={toDate}
          onChange={(e) => setToDate(e.target.value)}
        />
      </div>

      <p className="row-hint">{t('history.facts.count', { n: filtered.length })}</p>

      {error ? (
        <p className="field-error" role="alert">
          {error}
        </p>
      ) : null}

      {loading ? (
        <p className="muted">{t('common.loading')}</p>
      ) : filtered.length === 0 ? (
        <EmptyState icon={<IconSearch size={28} />} title={t('history.facts.empty')} />
      ) : (
        <div className="list">
          {filtered.map((fact) => (
            <div className="list-row" key={fact.factId}>
              <div className="list-main">
                <span className="list-title">{fact.fact}</span>
                <span className="list-sub">{formatDateTime(fact.timestamp)}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default FactsWindow
