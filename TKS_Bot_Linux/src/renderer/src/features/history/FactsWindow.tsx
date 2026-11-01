/**
 * 记忆档案列表（FR-HIS-2 / FR-HIS-3 / FR-HIS-4）。
 *
 * 独立组件：既用于历史页的「记忆档案」Tab，也用于 FR-DSK-8 的独立记忆窗口（`HistoryWindow`）。
 *
 * 本期可明确纠正用户事实、禁用和恢复；未提供彻底删除，避免造成
 * 「已删除但服务端仍保留」的错觉。
 */

import { useEffect, useMemo, useState } from 'react'
import type { MemoryDetail, QueuedMemoryPermission, UserFact } from '@shared/protocol'
import { Button, EmptyState, Input } from '../../components/primitives'
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
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<MemoryDetail | null>(null)
  const [pending, setPending] = useState<QueuedMemoryPermission[]>([])
  const [permissionBusy, setPermissionBusy] = useState(false)
  const [permissionMessage, setPermissionMessage] = useState<string | null>(null)
  const [correction, setCorrection] = useState('')
  const [correctionBusy, setCorrectionBusy] = useState(false)
  const [correctionMessage, setCorrectionMessage] = useState<string | null>(null)

  const selected = facts.find((fact) => fact.factId === selectedId)
  const stageLabel = (stage: string): string => stage === 'formal'
    ? t('history.facts.stage.formal') : t('history.facts.stage.candidate')
  const permissionLabel = (permission: string): string => permission === 'allowed'
    ? t('history.facts.permission.allowed') : t('history.facts.permission.blocked')
  const operationLabel = (status: string): string => status === 'applied'
    ? t('history.facts.permission.applied') : status === 'conflict'
      ? t('history.facts.permission.conflict') : t('history.facts.permission.pending')

  async function refresh(): Promise<void> {
    const [list, queued] = await Promise.all([
      window.tks.history.listFacts(300), window.tks.history.pendingPermissions()
    ])
    setFacts([...list].sort((a, b) => b.timestamp - a.timestamp))
    setPending(queued)
  }

  async function selectFact(factId: string): Promise<void> {
    setSelectedId(factId)
    setDetail(null)
    setPermissionMessage(null)
    setCorrection('')
    setCorrectionMessage(null)
    try {
      setDetail(await window.tks.history.factDetail(factId))
    } catch {
      setPermissionMessage(t('history.facts.detailUnavailable'))
    }
  }

  async function submitCorrection(): Promise<void> {
    if (!detail || detail.domain !== 'user_fact' || !correction.trim() || correction.trim() === detail.content) return
    setCorrectionBusy(true)
    try {
      await window.tks.history.correctFact(detail.factId, correction.trim(), detail.revision)
      await refresh()
      await selectFact(detail.factId)
      setCorrectionMessage(t('history.facts.correction.applied'))
    } catch (err) {
      await refresh().catch(() => undefined)
      setCorrectionMessage(describeError(err))
    } finally {
      setCorrectionBusy(false)
    }
  }

  async function changePermission(enabled: boolean): Promise<void> {
    if (!selected?.revision) return
    setPermissionBusy(true)
    try {
      const status = await window.tks.history.setFactPermission(selected.factId, enabled, selected.revision)
      await refresh()
      setPermissionMessage(operationLabel(status))
      if (status === 'applied') await selectFact(selected.factId)
    } catch (err) {
      setPermissionMessage(describeError(err))
    } finally {
      setPermissionBusy(false)
    }
  }

  useEffect(() => {
    let alive = true

    const load = async (): Promise<void> => {
      try {
        const [list, queued] = await Promise.all([
          window.tks.history.listFacts(300), window.tks.history.pendingPermissions()
        ])
        if (!alive) return
        // FR-HIS-2：按时间倒序
        setFacts([...list].sort((a, b) => b.timestamp - a.timestamp))
        setPending(queued)
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
      <p className="row-hint" role="note">
        <IconInfo size={16} /> {t('history.facts.managementHint')}
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
                <span className="list-sub">{formatDateTime(fact.timestamp)} · {stageLabel(fact.stage ?? 'candidate')} · {permissionLabel(fact.modelPermission ?? 'blocked')}</span>
              </div>
              <Button size="sm" onClick={() => void selectFact(fact.factId)}>{t('history.facts.view')}</Button>
            </div>
          ))}
        </div>
      )}
      {selected ? (
        <section className="section" aria-label={t('history.facts.detail')}>
          <h3>{t('history.facts.detail')}</h3>
          <p>{detail?.content ?? selected.fact}</p>
          <p className="row-hint">{t('history.facts.stageLabel')}: {stageLabel(detail?.stage ?? selected.stage ?? 'candidate')}</p>
          <p className="row-hint">{t('history.facts.truthLabel')}: {detail?.truthStatus ?? selected.truthStatus ?? 'pending'}</p>
          <p className="row-hint">{t('history.facts.scoreLabel')}: {detail?.score ?? selected.score ?? 0}</p>
          <p className="row-hint">{t('history.facts.revisionLabel')}: {detail?.revision ?? selected.revision ?? 0}</p>
          <p className="row-hint">{t('history.facts.sourceLabel')}: {detail?.sources?.map((source) => source.fragment || source.sourceMessageId).join('；') || selected.sourceMessageId || t('history.facts.unknown')}</p>
          <p className="row-hint">{t('history.facts.validityLabel')}: {JSON.stringify(detail?.temporalValidity ?? selected.temporalValidity ?? {})}</p>
          {pending.filter((item) => item.factId === selected.factId).map((item) => (
            <p key={item.operationId} role="status">{operationLabel(item.state)}</p>
          ))}
          {permissionMessage ? <p role="status">{permissionMessage}</p> : null}
          <Button disabled={permissionBusy || !selected.revision} onClick={() => void changePermission(selected.modelPermission === 'blocked')}>
            {selected.modelPermission === 'blocked' ? t('history.facts.restore') : t('history.facts.disable')}
          </Button>
          {detail?.domain === 'user_fact' ? (
            <div className="section">
              <Input label={t('history.facts.correction.label')} value={correction} maxLength={2000}
                onChange={(event) => setCorrection(event.target.value)} />
              <p className="row-hint">{t('history.facts.correction.hint')}</p>
              {correctionMessage ? <p role="status">{correctionMessage}</p> : null}
              <Button disabled={correctionBusy || !correction.trim() || correction.trim() === detail.content}
                onClick={() => void submitCorrection()}>{t('history.facts.correction.submit')}</Button>
            </div>
          ) : null}
        </section>
      ) : null}
    </div>
  )
}

export default FactsWindow
