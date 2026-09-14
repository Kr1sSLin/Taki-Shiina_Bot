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
}

function toFact(row: FactRow): UserFact {
  return { factId: row.fact_id, userId: row.user_id, fact: row.fact, timestamp: row.timestamp }
}

/** 幂等写入（`memory.fact.created` 可能重复到达）。 */
export function upsertFact(fact: UserFact): boolean {
  const db = getDb()
  const result = db
    .prepare(
      `INSERT INTO user_facts (fact_id, user_id, fact, timestamp)
       VALUES (?, ?, ?, ?)
       ON CONFLICT (fact_id) DO UPDATE SET fact = excluded.fact
       WHERE user_facts.fact <> excluded.fact`
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
