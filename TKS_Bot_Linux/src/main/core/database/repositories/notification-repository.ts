/**
 * Bot 通知 与 用户事实（记忆）仓储（§9.3 / §9.4）。
 */

import { createHash } from 'node:crypto'
import type { BotNotification, UserFact } from '@shared/protocol'
import { getDb } from '../db'

/* ------------------------------- bot_notifications ------------------------ */

interface NotificationRow {
  notification_id: string
  error_code: string
  message: string
  is_read: number
  timestamp: number
}

function toNotification(row: NotificationRow): BotNotification {
  return {
    notificationId: row.notification_id,
    errorCode: row.error_code,
    message: row.message,
    isRead: row.is_read === 1,
    timestamp: row.timestamp
  }
}

/**
 * 落库一条 Bot 错误通知（§5.3.2 `bot.error` 处理要求）。
 * 通知 ID 由错误码 + 时间戳 + 消息派生，同一次错误重复到达时幂等。
 */
export function insertNotification(input: {
  errorCode: string
  message: string
  timestamp: number
  notificationId?: string
}): BotNotification {
  const db = getDb()
  const id =
    input.notificationId ??
    `err_${createHash('sha1').update(`${input.errorCode}|${input.timestamp}|${input.message}`).digest('hex').slice(0, 20)}`
  db.prepare(
    `INSERT INTO bot_notifications (notification_id, error_code, message, is_read, timestamp)
     VALUES (?, ?, ?, 0, ?)
     ON CONFLICT (notification_id) DO NOTHING`
  ).run(id, input.errorCode, input.message, input.timestamp)
  return { notificationId: id, errorCode: input.errorCode, message: input.message, isRead: false, timestamp: input.timestamp }
}

export function listNotifications(limit = 200): BotNotification[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM bot_notifications ORDER BY timestamp DESC LIMIT ?')
    .all(limit) as NotificationRow[]
  return rows.map(toNotification)
}

export function unreadNotificationCount(): number {
  const db = getDb()
  const row = db.prepare('SELECT COUNT(*) AS n FROM bot_notifications WHERE is_read = 0').get() as { n: number }
  return row.n
}

export function markNotificationsRead(ids: string[]): void {
  if (ids.length === 0) return
  const db = getDb()
  const placeholders = ids.map(() => '?').join(',')
  db.prepare(`UPDATE bot_notifications SET is_read = 1 WHERE notification_id IN (${placeholders})`).run(...ids)
}

export function markAllNotificationsRead(): void {
  const db = getDb()
  db.prepare('UPDATE bot_notifications SET is_read = 1 WHERE is_read = 0').run()
}

/* --------------------------------- user_facts ----------------------------- */

interface FactRow {
  fact_id: string
  user_id: string
  fact: string
  timestamp: number
  revision: number
  revision_id: string | null
  permission_version: number
  model_permission: string
  stage: string
  truth_status: string
  revision_status: string
  score: number
  source_message_id: string | null
  first_recorded_at: number | null
  temporal_validity: string | null
}

function toFact(row: FactRow): UserFact {
  return {
    factId: row.fact_id, userId: row.user_id, fact: row.fact, timestamp: row.timestamp,
    revision: row.revision, revisionId: row.revision_id,
    permissionVersion: row.permission_version,
    modelPermission: row.model_permission as UserFact['modelPermission'],
    stage: row.stage as UserFact['stage'], truthStatus: row.truth_status as UserFact['truthStatus'],
    revisionStatus: row.revision_status as UserFact['revisionStatus'], score: row.score,
    sourceMessageId: row.source_message_id, firstRecordedAt: row.first_recorded_at,
    temporalValidity: row.temporal_validity ? JSON.parse(row.temporal_validity) as Record<string, unknown> : null
  }
}

/** 幂等写入（`memory.fact.created` 可能重复到达）。 */
export function upsertFact(fact: UserFact): boolean {
  const db = getDb()
  const result = db
    .prepare(
      `INSERT INTO user_facts (fact_id, user_id, fact, timestamp)
       VALUES (?, ?, ?, ?)
       ON CONFLICT (fact_id) DO UPDATE SET fact = excluded.fact
       WHERE user_facts.revision = 0 AND user_facts.fact <> excluded.fact`
    )
    .run(fact.factId, fact.userId, fact.fact, fact.timestamp)
  return result.changes > 0
}

export function bulkUpsertFacts(facts: UserFact[]): number {
  if (facts.length === 0) return 0
  const db = getDb()
  const stmt = db.prepare(
    `INSERT INTO user_facts (fact_id, user_id, fact, timestamp)
     VALUES (?, ?, ?, ?)
     ON CONFLICT (fact_id) DO NOTHING`
  )
  let inserted = 0
  const tx = db.transaction(() => {
    for (const f of facts) inserted += stmt.run(f.factId, f.userId, f.fact, f.timestamp).changes
  })
  tx()
  return inserted
}

/** Apply a server page and its cursor as one SQLite transaction. */
export function applyFactPage(items: UserFact[], nextCursor: number): UserFact[] {
  const db = getDb()
  const changed: UserFact[] = []
  const insertEvent = db.prepare('INSERT OR IGNORE INTO memory_seen_events (event_id, sequence) VALUES (?, ?)')
  const current = db.prepare('SELECT revision, permission_version FROM user_facts WHERE fact_id = ?')
  const upsert = db.prepare(`
    INSERT INTO user_facts (fact_id,user_id,fact,timestamp,revision,revision_id,permission_version,
      model_permission,stage,truth_status,revision_status,score,source_message_id,first_recorded_at,temporal_validity)
    VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
    ON CONFLICT(fact_id) DO UPDATE SET
      fact=excluded.fact,timestamp=excluded.timestamp,revision=excluded.revision,
      revision_id=excluded.revision_id,permission_version=excluded.permission_version,
      model_permission=excluded.model_permission,stage=excluded.stage,
      truth_status=excluded.truth_status,revision_status=excluded.revision_status,
      score=excluded.score,source_message_id=excluded.source_message_id,
      first_recorded_at=excluded.first_recorded_at,temporal_validity=excluded.temporal_validity
  `)
  const advance = db.prepare(`INSERT INTO sync_cursors (key,value) VALUES ('memory_events',?)
    ON CONFLICT(key) DO UPDATE SET value=MAX(value,excluded.value)`)
  db.transaction(() => {
    for (const fact of items) {
      if (!fact.eventId || typeof fact.sequence !== 'number' || !Number.isSafeInteger(fact.sequence) ||
          !fact.factId || typeof fact.revision !== 'number' || !Number.isInteger(fact.revision)) {
        throw new Error('Invalid memory event page')
      }
      if (insertEvent.run(fact.eventId, fact.sequence).changes === 0) continue
      const old = current.get(fact.factId) as { revision: number; permission_version: number } | undefined
      if (old && (old.revision > fact.revision! ||
        (old.revision === fact.revision && old.permission_version >= (fact.permissionVersion ?? 0)))) continue
      upsert.run(fact.factId, fact.userId, fact.fact, fact.timestamp, fact.revision,
        fact.revisionId ?? null, fact.permissionVersion ?? 0, fact.modelPermission ?? 'blocked',
        fact.stage ?? 'candidate', fact.truthStatus ?? 'pending', fact.revisionStatus ?? 'active',
        fact.score ?? 0, fact.sourceMessageId ?? null, fact.firstRecordedAt ?? null,
        fact.temporalValidity ? JSON.stringify(fact.temporalValidity) : null)
      changed.push(fact)
    }
    advance.run(nextCursor)
  })()
  return changed
}

export interface QueuedPermission {
  operationId: string
  owner: string
  factId: string
  enabled: boolean
  expectedRevision: number
  state: 'pending' | 'conflict'
}

export function queuePermission(item: Omit<QueuedPermission, 'state'>): void {
  getDb().prepare(`INSERT INTO memory_permission_queue
    (operation_id,owner,fact_id,enabled,expected_revision,state,created_at)
    VALUES (?,?,?,?,?,'pending',?)`).run(
      item.operationId, item.owner, item.factId, Number(item.enabled), item.expectedRevision, Date.now())
}

export function listQueuedPermissions(owner: string): QueuedPermission[] {
  const rows = getDb().prepare(`SELECT * FROM memory_permission_queue
    WHERE owner=? ORDER BY created_at,operation_id`).all(owner) as Array<{
      operation_id: string; owner: string; fact_id: string; enabled: number;
      expected_revision: number; state: 'pending' | 'conflict'
    }>
  return rows.map((row) => ({ operationId: row.operation_id, owner: row.owner,
    factId: row.fact_id, enabled: row.enabled === 1,
    expectedRevision: row.expected_revision, state: row.state }))
}

export function finishPermission(operationId: string, state: 'done' | 'conflict'): void {
  if (state === 'done') {
    getDb().prepare('DELETE FROM memory_permission_queue WHERE operation_id=?').run(operationId)
  } else {
    getDb().prepare("UPDATE memory_permission_queue SET state='conflict' WHERE operation_id=?").run(operationId)
  }
}

export function listFacts(limit = 300): UserFact[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM user_facts ORDER BY timestamp DESC LIMIT ?')
    .all(limit) as FactRow[]
  return rows.map(toFact)
}

/** FR-HIS-3：记忆搜索 + 按日期筛选（日期为服务端时间戳区间，此处按本地渲染日期过滤由渲染层完成）。 */
export function searchFacts(keyword: string, sinceMs: number | null, untilMs: number | null, limit = 300): UserFact[] {
  const db = getDb()
  const clauses: string[] = []
  const params: unknown[] = []
  const term = (keyword ?? '').trim()
  if (term) {
    clauses.push('fact LIKE ?')
    params.push(`%${term}%`)
  }
  if (sinceMs !== null) {
    clauses.push('timestamp >= ?')
    params.push(sinceMs)
  }
  if (untilMs !== null) {
    clauses.push('timestamp <= ?')
    params.push(untilMs)
  }
  const where = clauses.length ? `WHERE ${clauses.join(' AND ')}` : ''
  params.push(limit)
  const rows = db
    .prepare(`SELECT * FROM user_facts ${where} ORDER BY timestamp DESC LIMIT ?`)
    .all(...params) as FactRow[]
  return rows.map(toFact)
}

export function maxFactTimestamp(): number {
  const db = getDb()
  const row = db.prepare('SELECT MAX(timestamp) AS ts FROM user_facts').get() as { ts: number | null }
  return row.ts ?? 0
}

export function countFacts(): number {
  const db = getDb()
  const row = db.prepare('SELECT COUNT(*) AS n FROM user_facts').get() as { n: number }
  return row.n
}
