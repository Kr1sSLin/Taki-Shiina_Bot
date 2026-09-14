/**
 * 积分 / 等级 / 补签卡 离线缓存仓储（§9.6）。
 *
 * ⚠️ FR-DB-2：以上表**仅为缓存**，任何写操作都不得作为业务事实来源（FR-PROG-1）。
 * ⚠️ FR-DB-4：列名风格与 §7.0 字段名契约一致，**不做统一驼峰转换**：
 *    - `user_progress_cache` / `makeup_candidates_cache`   → camelCase 语义
 *    - `points_ledger_cache` / `makeup_cards_cache`        → snake_case（直接映射服务端 DTO）
 */

import type {
  LevelConfigData,
  LevelConfigEntry,
  MakeupCandidate,
  MakeupCardRecord,
  MakeupCardSummary,
  PointsLedgerEntry,
  PointsOverviewData,
  UserProgressCache
} from '@shared/protocol'
import { getDb } from '../db'

/* -------------------------------------------------------------------------- */
/* user_progress_cache                                                          */
/* -------------------------------------------------------------------------- */

interface ProgressRow {
  user_id: string
  balance: number
  balance_updated_at: number
  level_code: string
  level_name: string
  prev_level_code: string | null
  highest_level_code: string | null
  continuous_days: number
  next_level_code: string | null
  next_level_name: string | null
  next_level_threshold_days: number | null
  days_to_next_level: number | null
  last_valid_date: string | null
  gap_days: number
  break_deadline_date: string | null
  is_default_level: number
  level_updated_at: number | null
  available_makeup_cards: number
  synced_at: number
}

function toProgress(row: ProgressRow): UserProgressCache {
  return {
    userId: row.user_id,
    balance: row.balance,
    balanceUpdatedAt: row.balance_updated_at,
    levelCode: row.level_code,
    levelName: row.level_name,
    prevLevelCode: row.prev_level_code,
    highestLevelCode: row.highest_level_code,
    continuousDays: row.continuous_days,
    nextLevelCode: row.next_level_code,
    nextLevelName: row.next_level_name,
    nextLevelThresholdDays: row.next_level_threshold_days,
    daysToNextLevel: row.days_to_next_level,
    lastValidDate: row.last_valid_date,
    gapDays: row.gap_days,
    breakDeadlineDate: row.break_deadline_date,
    isDefaultLevel: row.is_default_level === 1,
    levelUpdatedAt: row.level_updated_at,
    availableMakeupCards: row.available_makeup_cards,
    syncedAt: row.synced_at
  }
}

/** 整体覆盖写入（登录 / 重连 / 收到事件时调用，FR-PT-5）。 */
export function saveProgressFromOverview(userId: string, overview: PointsOverviewData): void {
  const db = getDb()
  const l = overview.level
  const now = Date.now()
  db.prepare(
    `INSERT INTO user_progress_cache (
        user_id, balance, balance_updated_at, level_code, level_name, prev_level_code,
        highest_level_code, continuous_days, next_level_code, next_level_name,
        next_level_threshold_days, days_to_next_level, last_valid_date, gap_days,
        break_deadline_date, is_default_level, level_updated_at, available_makeup_cards, synced_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (user_id) DO UPDATE SET
        balance = excluded.balance,
        balance_updated_at = excluded.balance_updated_at,
        level_code = excluded.level_code,
        level_name = excluded.level_name,
        prev_level_code = excluded.prev_level_code,
        highest_level_code = excluded.highest_level_code,
        continuous_days = excluded.continuous_days,
        next_level_code = excluded.next_level_code,
        next_level_name = excluded.next_level_name,
        next_level_threshold_days = excluded.next_level_threshold_days,
        days_to_next_level = excluded.days_to_next_level,
        last_valid_date = excluded.last_valid_date,
        gap_days = excluded.gap_days,
        break_deadline_date = excluded.break_deadline_date,
        is_default_level = excluded.is_default_level,
        level_updated_at = excluded.level_updated_at,
        available_makeup_cards = excluded.available_makeup_cards,
        synced_at = excluded.synced_at`
  ).run(
    userId,
    overview.balance,
    overview.balanceUpdatedAt ?? now,
    l.levelCode ?? '',
    l.levelName ?? '',
    l.prevLevelCode ?? null,
    l.highestLevelCode ?? null,
    l.continuousDays ?? 0,
    l.nextLevelCode ?? null,
    l.nextLevelName ?? null,
    l.nextLevelThresholdDays ?? null,
    l.daysToNextLevel ?? null,
    l.lastValidDate ?? null,
    l.gapDays ?? 0,
    l.breakDeadlineDate ?? null,
    l.isDefaultLevel ? 1 : 0,
    l.levelUpdatedAt ?? null,
    l.availableMakeupCards ?? overview.makeupCard?.available ?? 0,
    now
  )
}

export function getProgress(userId: string): UserProgressCache | null {
  const db = getDb()
  const row = db.prepare('SELECT * FROM user_progress_cache WHERE user_id = ?').get(userId) as ProgressRow | undefined
  return row ? toProgress(row) : null
}

/** 事件驱动的最小覆盖（`points.changed` / `points.snapshot`）。 */
export function patchBalance(userId: string, balance: number, updatedAt = Date.now()): void {
  const db = getDb()
  db.prepare(
    `INSERT INTO user_progress_cache (user_id, balance, balance_updated_at, synced_at)
     VALUES (?, ?, ?, ?)
     ON CONFLICT (user_id) DO UPDATE SET
        balance = excluded.balance,
        balance_updated_at = excluded.balance_updated_at,
        synced_at = excluded.synced_at`
  ).run(userId, balance, updatedAt, Date.now())
}

/** `makeup_card.changed` 的最小覆盖（§7.4 FR-MC-8）。 */
export function patchMakeupAvailable(userId: string, available: number): void {
  const db = getDb()
  db.prepare(
    `INSERT INTO user_progress_cache (user_id, available_makeup_cards, synced_at)
     VALUES (?, ?, ?)
     ON CONFLICT (user_id) DO UPDATE SET
        available_makeup_cards = excluded.available_makeup_cards,
        synced_at = excluded.synced_at`
  ).run(userId, available, Date.now())
}

/* -------------------------------------------------------------------------- */
/* points_ledger_cache（snake_case 列，FR-DB-4）                                 */
/* -------------------------------------------------------------------------- */

interface LedgerRow {
  id: number
  user_id: string
  change_amount: number
  reason_code: string
  balance_after: number
  related_item_id: string | null
  idempotency_key: string | null
  created_at: number
  business_date: string
}

function toLedger(row: LedgerRow): PointsLedgerEntry {
  return {
    id: row.id,
    user_id: row.user_id,
    change_amount: row.change_amount,
    reason_code: row.reason_code,
    balance_after: row.balance_after,
    related_item_id: row.related_item_id,
    idempotency_key: row.idempotency_key ?? '',
    created_at: row.created_at,
    business_date: row.business_date
  }
}

export function cacheLedgerEntries(entries: PointsLedgerEntry[], page: number): void {
  if (entries.length === 0) return
  const db = getDb()
  const stmt = db.prepare(
    `INSERT INTO points_ledger_cache
       (id, user_id, change_amount, reason_code, balance_after, related_item_id, idempotency_key, created_at, business_date)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET
       change_amount = excluded.change_amount,
       reason_code = excluded.reason_code,
       balance_after = excluded.balance_after,
       related_item_id = excluded.related_item_id,
       created_at = excluded.created_at,
       business_date = excluded.business_date`
  )
  const tx = db.transaction(() => {
    for (const e of entries) {
      stmt.run(
        e.id,
        e.user_id,
        e.change_amount,
        e.reason_code,
        e.balance_after,
        e.related_item_id ?? null,
        e.idempotency_key ?? null,
        e.created_at,
        e.business_date
      )
    }
  })
  tx()
  void page
}

export function getCachedLedger(limit = 300): PointsLedgerEntry[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM points_ledger_cache ORDER BY created_at DESC LIMIT ?')
    .all(limit) as LedgerRow[]
  return rows.map(toLedger)
}

/* -------------------------------------------------------------------------- */
/* makeup_cards_cache（snake_case 列，FR-DB-4）                                  */
/* -------------------------------------------------------------------------- */

interface CardRow {
  id: number
  user_id: string
  granted_month: string
  status: string
  used_for_date: string | null
  used_at: number | null
  created_at: number
}

function toCard(row: CardRow): MakeupCardRecord {
  return {
    id: row.id,
    user_id: row.user_id,
    granted_month: row.granted_month,
    status: row.status,
    used_for_date: row.used_for_date,
    used_at: row.used_at,
    created_at: row.created_at
  }
}

export function cacheMakeupCards(records: MakeupCardRecord[]): void {
  if (records.length === 0) return
  const db = getDb()
  const stmt = db.prepare(
    `INSERT INTO makeup_cards_cache (id, user_id, granted_month, status, used_for_date, used_at, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?)
     ON CONFLICT (id) DO UPDATE SET
       status = excluded.status,
       used_for_date = excluded.used_for_date,
       used_at = excluded.used_at`
  )
  const tx = db.transaction(() => {
    for (const r of records) {
      stmt.run(r.id, r.user_id, r.granted_month, r.status, r.used_for_date ?? null, r.used_at ?? null, r.created_at)
    }
  })
  tx()
}

export function getCachedMakeupCards(limit = 200): MakeupCardRecord[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM makeup_cards_cache ORDER BY created_at DESC LIMIT ?')
    .all(limit) as CardRow[]
  return rows.map(toCard)
}

/* -------------------------------------------------------------------------- */
/* makeup_candidates_cache                                                      */
/* -------------------------------------------------------------------------- */

export function replaceMakeupCandidates(items: MakeupCandidate[]): void {
  const db = getDb()
  const now = Date.now()
  const tx = db.transaction(() => {
    db.prepare('DELETE FROM makeup_candidates_cache').run()
    const stmt = db.prepare(
      'INSERT INTO makeup_candidates_cache (date, days_ago, synced_at) VALUES (?, ?, ?)'
    )
    for (const item of items) stmt.run(item.date, item.daysAgo, now)
  })
  tx()
}

export function getCachedMakeupCandidates(): MakeupCandidate[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT date, days_ago FROM makeup_candidates_cache ORDER BY date DESC')
    .all() as Array<{ date: string; days_ago: number }>
  return rows.map((r) => ({ date: r.date, daysAgo: r.days_ago }))
}

/* -------------------------------------------------------------------------- */
/* level_config_cache（snake_case 字段，EDGE-L24 防御）                          */
/* -------------------------------------------------------------------------- */

export function replaceLevelConfig(config: LevelConfigData): void {
  const db = getDb()
  const now = Date.now()
  const tx = db.transaction(() => {
    db.prepare('DELETE FROM level_config_cache').run()
    const stmt = db.prepare(
      `INSERT INTO level_config_cache (level_code, level_name, threshold_days, sort_order, synced_at)
       VALUES (?, ?, ?, ?, ?)`
    )
    for (const lv of config.levels ?? []) {
      // EDGE-L24：对 level_code 为空做防御，绝不让空 key 入库
      if (!lv?.level_code) continue
      stmt.run(lv.level_code, lv.level_name ?? '', lv.threshold_days ?? 0, lv.sort_order ?? 0, now)
    }
  })
  tx()
}

export function getCachedLevelConfig(): LevelConfigData | null {
  const db = getDb()
  const rows = db
    .prepare('SELECT level_code, level_name, threshold_days, sort_order FROM level_config_cache ORDER BY sort_order ASC')
    .all() as LevelConfigEntry[]
  if (rows.length === 0) return null
  return {
    levels: rows,
    defaultLevelCode: 'NONE',
    defaultLevelName: '',
    makeupCardMax: 12,
    breakGapDays: 5,
    warningGapDays: [3, 4]
  }
}

/** 缓存清理（切换账号或完全重置时）。 */
export function clearProgressCaches(userId?: string): void {
  const db = getDb()
  const tx = db.transaction(() => {
    if (userId) {
      db.prepare('DELETE FROM user_progress_cache WHERE user_id = ?').run(userId)
      db.prepare('DELETE FROM points_ledger_cache WHERE user_id = ?').run(userId)
      db.prepare('DELETE FROM makeup_cards_cache WHERE user_id = ?').run(userId)
    } else {
      db.prepare('DELETE FROM user_progress_cache').run()
      db.prepare('DELETE FROM points_ledger_cache').run()
      db.prepare('DELETE FROM makeup_cards_cache').run()
    }
    db.prepare('DELETE FROM makeup_candidates_cache').run()
  })
  tx()
}

export type { MakeupCardSummary }
