/**
 * 提醒仓储（§9.5，FR-REM-4）。
 *
 * Android 靠 WorkManager 做系统级持久化，Electron 无等价能力，因此必须自行落库，
 * 并在应用重启后重新装载未触发的提醒。
 */

import type { ReminderRecord } from '@shared/protocol'
import { getDb } from '../db'

interface ReminderRow {
  reminder_id: string
  target_time: string
  fire_at: number
  text: string
  status: string
  created_at: number
}

function toReminder(row: ReminderRow): ReminderRecord {
  return {
    reminderId: row.reminder_id,
    targetTime: row.target_time,
    fireAt: row.fire_at,
    text: row.text,
    status: row.status as ReminderRecord['status'],
    createdAt: row.created_at
  }
}

/** FR-REM-3：同键已存在则保留原计划（`ExistingWorkPolicy.KEEP` 语义）。 */
export function insertReminderKeep(input: {
  reminderId: string
  targetTime: string
  fireAt: number
  text: string
}): { created: boolean; record: ReminderRecord | null } {
  const db = getDb()
  const existing = db.prepare('SELECT * FROM reminders WHERE reminder_id = ?').get(input.reminderId) as
    | ReminderRow
    | undefined
  if (existing) {
    // 保留原计划：若原计划已被取消/已触发则不再复活
    return { created: false, record: toReminder(existing) }
  }
  const createdAt = Date.now()
  db.prepare(
    `INSERT INTO reminders (reminder_id, target_time, fire_at, text, status, created_at)
     VALUES (?, ?, ?, ?, 'pending', ?)`
  ).run(input.reminderId, input.targetTime, input.fireAt, input.text, createdAt)
  return {
    created: true,
    record: {
      reminderId: input.reminderId,
      targetTime: input.targetTime,
      fireAt: input.fireAt,
      text: input.text,
      status: 'pending',
      createdAt
    }
  }
}

export function listPendingReminders(): ReminderRecord[] {
  const db = getDb()
  const rows = db
    .prepare(`SELECT * FROM reminders WHERE status = 'pending' ORDER BY fire_at ASC`)
    .all() as ReminderRow[]
  return rows.map(toReminder)
}

export function listReminders(limit = 200): ReminderRecord[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM reminders ORDER BY fire_at DESC LIMIT ?')
    .all(limit) as ReminderRow[]
  return rows.map(toReminder)
}

export function setReminderStatus(reminderId: string, status: ReminderRecord['status']): void {
  const db = getDb()
  db.prepare('UPDATE reminders SET status = ? WHERE reminder_id = ?').run(status, reminderId)
}

export function cancelReminder(reminderId: string): boolean {
  const db = getDb()
  return (
    db.prepare(`UPDATE reminders SET status = 'cancelled' WHERE reminder_id = ? AND status = 'pending'`).run(reminderId)
      .changes > 0
  )
}

export function getReminder(reminderId: string): ReminderRecord | null {
  const db = getDb()
  const row = db.prepare('SELECT * FROM reminders WHERE reminder_id = ?').get(reminderId) as ReminderRow | undefined
  return row ? toReminder(row) : null
}

/** 清理 30 天前的终态提醒，避免表无限增长。 */
export function pruneReminders(): number {
  const db = getDb()
  const cutoff = Date.now() - 30 * 24 * 3600 * 1000
  return db.prepare(`DELETE FROM reminders WHERE status <> 'pending' AND created_at < ?`).run(cutoff).changes
}
