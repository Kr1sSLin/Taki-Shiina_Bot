/**
 * 本地数据库（§9，对标 Android Room v4 五张表 + 积分缓存表）。
 *
 * 关键约束：
 * - FR-DB-1：更新消息行**禁止** `INSERT OR REPLACE`（会触发外键 CASCADE 删掉附件行）。
 * - FR-DB-2：四张积分缓存表**仅为缓存**，任何写操作都不是业务事实来源（FR-PROG-1）。
 * - FR-DB-3：`user_version` pragma 管理 schema 版本，顺序迁移；失败时备份原库。
 * - FR-DB-4：缓存表容忍字段名风格混用（`points_ledger_cache` / `makeup_cards_cache` 保持 snake_case）。
 * - NFR-13：WAL 模式 + 事务；主进程崩溃不得损坏数据库。
 * - EDGE-L14：启动时 `PRAGMA integrity_check`，失败则备份并重建空库。
 */

import Database from 'better-sqlite3'
import { copyFileSync, existsSync, mkdirSync, renameSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { createLogger } from '../../app/logger'

const log = createLogger('db')

export type Db = Database.Database

interface Migration {
  version: number
  name: string
  up: (db: Db) => void
}

/* -------------------------------------------------------------------------- */
/* 迁移脚本（从 v1 起顺序执行）                                                  */
/* -------------------------------------------------------------------------- */

const MIGRATIONS: Migration[] = [
  {
    version: 1,
    name: 'baseline-room-v4-tables',
    up: (db) => {
      // §9.1 chat_messages
      db.exec(`
        CREATE TABLE IF NOT EXISTS chat_messages (
          message_id     TEXT PRIMARY KEY,
          session_id     TEXT NOT NULL,
          role           TEXT NOT NULL,
          message_type   TEXT NOT NULL DEFAULT 'text',
          content_type   TEXT NOT NULL DEFAULT 'text',
          model_provider TEXT,
          content        TEXT NOT NULL DEFAULT '',
          status         TEXT NOT NULL DEFAULT 'received',
          timestamp      INTEGER NOT NULL,
          error_code     TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_chat_messages_session_ts
          ON chat_messages (session_id, timestamp);
        CREATE INDEX IF NOT EXISTS idx_chat_messages_status
          ON chat_messages (status);
      `)

      // §9.2 chat_attachments（外键级联）
      // ⚠️ `message_id` 允许 NULL：表示「已选中但尚未发送」的草稿附件。
      //    图片在选中时就已复制进应用私有目录（FR-IMG-5），因此草稿也必须持久化，
      //    否则应用在发送前退出会丢附件。NULL 不违背外键级联语义（级联只对非 NULL 生效）。
      db.exec(`
        CREATE TABLE IF NOT EXISTS chat_attachments (
          attachment_id TEXT PRIMARY KEY,
          message_id    TEXT REFERENCES chat_messages (message_id) ON DELETE CASCADE,
          session_id    TEXT NOT NULL,
          mime_type     TEXT NOT NULL,
          local_path    TEXT NOT NULL,
          file_size     INTEGER NOT NULL DEFAULT 0,
          width         INTEGER,
          height        INTEGER,
          upload_state  TEXT NOT NULL DEFAULT 'local',
          timestamp     INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_chat_attachments_message
          ON chat_attachments (message_id);
      `)

      // §9.3 bot_notifications
      db.exec(`
        CREATE TABLE IF NOT EXISTS bot_notifications (
          notification_id TEXT PRIMARY KEY,
          error_code      TEXT NOT NULL,
          message         TEXT NOT NULL,
          is_read         INTEGER NOT NULL DEFAULT 0,
          timestamp       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_bot_notifications_ts
          ON bot_notifications (is_read, timestamp);
      `)

      // §9.4 user_facts
      db.exec(`
        CREATE TABLE IF NOT EXISTS user_facts (
          fact_id   TEXT PRIMARY KEY,
          user_id   TEXT NOT NULL,
          fact      TEXT NOT NULL,
          timestamp INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_user_facts_user_ts
          ON user_facts (user_id, timestamp);
      `)

      // §9.5 reminders（替代 Android WorkManager 持久化，FR-REM-4）
      db.exec(`
        CREATE TABLE IF NOT EXISTS reminders (
          reminder_id TEXT PRIMARY KEY,
          target_time TEXT NOT NULL,
          fire_at     INTEGER NOT NULL,
          text        TEXT NOT NULL,
          status      TEXT NOT NULL DEFAULT 'pending',
          created_at  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_reminders_status_fire
          ON reminders (status, fire_at);
      `)

      // 同步游标（FR-SYNC-3 / FR-CHAT-12 / FR-SYNC-7 需要独立游标）
      db.exec(`
        CREATE TABLE IF NOT EXISTS sync_cursors (
          key        TEXT PRIMARY KEY,
          value      INTEGER NOT NULL
        );
      `)

      // 已投递 Bot 消息去重（FR-INT-12 / EDGE-L21）
      db.exec(`
        CREATE TABLE IF NOT EXISTS delivered_bot_messages (
          message_id TEXT PRIMARY KEY,
          timestamp  INTEGER NOT NULL
        );
      `)
    }
  },
  {
    version: 2,
    name: 'gamification-cache-tables',
    up: (db) => {
      // §9.6 user_progress_cache（camelCase 语义）
      db.exec(`
        CREATE TABLE IF NOT EXISTS user_progress_cache (
          user_id                  TEXT PRIMARY KEY,
          balance                  INTEGER NOT NULL DEFAULT 0,
          balance_updated_at       INTEGER NOT NULL DEFAULT 0,
          level_code               TEXT NOT NULL DEFAULT '',
          level_name               TEXT NOT NULL DEFAULT '',
          prev_level_code          TEXT,
          highest_level_code       TEXT,
          continuous_days          INTEGER NOT NULL DEFAULT 0,
          next_level_code          TEXT,
          next_level_name          TEXT,
          next_level_threshold_days INTEGER,
          days_to_next_level       INTEGER,
          last_valid_date          TEXT,
          gap_days                 INTEGER NOT NULL DEFAULT 0,
          break_deadline_date      TEXT,
          is_default_level         INTEGER NOT NULL DEFAULT 1,
          level_updated_at         INTEGER,
          available_makeup_cards   INTEGER NOT NULL DEFAULT 0,
          synced_at                INTEGER NOT NULL DEFAULT 0
        );
      `)

      // §9.6 points_ledger_cache（**snake_case**，直接映射服务端 DTO，FR-DB-4）
      db.exec(`
        CREATE TABLE IF NOT EXISTS points_ledger_cache (
          id               INTEGER PRIMARY KEY,
          user_id          TEXT NOT NULL,
          change_amount    INTEGER NOT NULL,
          reason_code      TEXT NOT NULL,
          balance_after    INTEGER NOT NULL,
          related_item_id  TEXT,
          idempotency_key  TEXT,
          created_at       INTEGER NOT NULL,
          business_date    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_points_ledger_cache_created
          ON points_ledger_cache (user_id, created_at DESC);
      `)

      // §9.6 makeup_cards_cache（**snake_case**，FR-DB-4）
      db.exec(`
        CREATE TABLE IF NOT EXISTS makeup_cards_cache (
          id            INTEGER PRIMARY KEY,
          user_id       TEXT NOT NULL,
          granted_month TEXT NOT NULL,
          status        TEXT NOT NULL,
          used_for_date TEXT,
          used_at       INTEGER,
          created_at    INTEGER NOT NULL
        );
      `)

      // §9.6 makeup_candidates_cache（camelCase 语义）
      db.exec(`
        CREATE TABLE IF NOT EXISTS makeup_candidates_cache (
          date      TEXT PRIMARY KEY,
          days_ago  INTEGER NOT NULL,
          synced_at INTEGER NOT NULL
        );
      `)

      // 等级阈值表缓存（`/level/config`，snake_case 字段，EDGE-L24）
      db.exec(`
        CREATE TABLE IF NOT EXISTS level_config_cache (
          level_code     TEXT PRIMARY KEY,
          level_name     TEXT NOT NULL,
          threshold_days INTEGER NOT NULL,
          sort_order     INTEGER NOT NULL,
          synced_at      INTEGER NOT NULL
        );
      `)
    }
  },
  {
    version: 3,
    name: 'chat-messages-fts5',
    up: (db) => {
      // FR-CHAT-17：全文搜索本地消息
      db.exec(`
        CREATE VIRTUAL TABLE IF NOT EXISTS chat_messages_fts
          USING fts5(content, message_id UNINDEXED, tokenize='unicode61');
      `)
      db.exec(`
        INSERT INTO chat_messages_fts (content, message_id)
        SELECT content, message_id FROM chat_messages WHERE content <> '';
      `)
      // 同步触发器
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_insert
        AFTER INSERT ON chat_messages BEGIN
          INSERT INTO chat_messages_fts (content, message_id) VALUES (new.content, new.message_id);
        END;
      `)
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_update
        AFTER UPDATE OF content ON chat_messages BEGIN
          DELETE FROM chat_messages_fts WHERE message_id = old.message_id;
          INSERT INTO chat_messages_fts (content, message_id) VALUES (new.content, new.message_id);
        END;
      `)
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_delete
        AFTER DELETE ON chat_messages BEGIN
          DELETE FROM chat_messages_fts WHERE message_id = old.message_id;
        END;
      `)
    }
  },
  {
    version: 4,
    name: 'reminder-dedupe-and-attachment-index',
    up: (db) => {
      // FR-REM-3 去重键即主键，此处补一个便于「同 target 同日」查询的索引
      db.exec(`
        CREATE INDEX IF NOT EXISTS idx_reminders_target_time
          ON reminders (target_time, status);
      `)
      db.exec(`
        CREATE INDEX IF NOT EXISTS idx_chat_messages_role_ts
          ON chat_messages (role, timestamp);
      `)
    }
  },
  {
    version: 5,
    name: 'chat-messages-trigram-index-for-cjk',
    up: (db) => {
      /*
       * FR-CHAT-17 修正：FTS5 的 `unicode61` 分词器**不能对中文做子串匹配**。
       * 实测（SQLite 3.49.2）：
       *   内容「今天写了一个很长的递归函数」
       *   MATCH '"今天写了一个很长的递归函数"' → 命中（整串被当作一个 token）
       *   MATCH '"递归函数"'                   → **不命中**
       * 即用户搜索任意中文片段都会静默返回空结果。
       *
       * 因此额外建立一份 `trigram` 索引：它按 3 字符滑窗建索引，
       * 对中文片段与拉丁子串都能命中（实测 MATCH '"递归函数"' → 命中）。
       * 代价是查询词需 ≥3 个字符；1~2 字的查询由 `searchMessages` 回退到 LIKE。
       */
      db.exec(`
        CREATE VIRTUAL TABLE IF NOT EXISTS chat_messages_fts_tri
          USING fts5(content, message_id UNINDEXED, tokenize='trigram');
      `)
      db.exec(`
        INSERT INTO chat_messages_fts_tri (content, message_id)
        SELECT content, message_id FROM chat_messages WHERE content <> '';
      `)
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_tri_insert
        AFTER INSERT ON chat_messages BEGIN
          INSERT INTO chat_messages_fts_tri (content, message_id) VALUES (new.content, new.message_id);
        END;
      `)
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_tri_update
        AFTER UPDATE OF content ON chat_messages BEGIN
          DELETE FROM chat_messages_fts_tri WHERE message_id = old.message_id;
          INSERT INTO chat_messages_fts_tri (content, message_id) VALUES (new.content, new.message_id);
        END;
      `)
      db.exec(`
        CREATE TRIGGER IF NOT EXISTS trg_chat_messages_fts_tri_delete
        AFTER DELETE ON chat_messages BEGIN
          DELETE FROM chat_messages_fts_tri WHERE message_id = old.message_id;
        END;
      `)
    }
  }
]

export const SCHEMA_VERSION = MIGRATIONS[MIGRATIONS.length - 1].version

/* -------------------------------------------------------------------------- */
/* 打开 / 完整性校验 / 迁移                                                      */
/* -------------------------------------------------------------------------- */

function backupDatabase(dbPath: string, suffix: string): string | null {
  try {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-')
    const backup = `${dbPath}.${suffix}-${stamp}.bak`
    copyFileSync(dbPath, backup)
    log.warn('已备份数据库', { backup })
    return backup
  } catch (err) {
    log.error('备份数据库失败', { error: String(err) })
    return null
  }
}

/** EDGE-L14：损坏时备份 + 重建空库，随后靠全量同步恢复服务端侧数据。 */
export type IntegrityResult = 'ok' | 'recovered'

function openRaw(dbPath: string): Db {
  const db = new Database(dbPath)
  db.pragma('journal_mode = WAL')
  db.pragma('synchronous = NORMAL')
  db.pragma('foreign_keys = ON')
  db.pragma('busy_timeout = 5000')
  return db
}

function runMigrations(db: Db): void {
  const current = db.pragma('user_version', { simple: true }) as number
  const pending = MIGRATIONS.filter((m) => m.version > current).sort((a, b) => a.version - b.version)
  if (pending.length === 0) return

  for (const migration of pending) {
    log.info('执行迁移', { version: migration.version, name: migration.name })
    const tx = db.transaction(() => {
      migration.up(db)
      db.pragma(`user_version = ${migration.version}`)
    })
    tx()
  }
  log.info('迁移完成', { from: current, to: SCHEMA_VERSION })
}

let instance: Db | null = null

export interface InitDbOptions {
  /** 迁移失败时是否自动备份并重建（EDGE-L14 / FR-DB-3）。 */
  recoverOnFailure?: boolean
  /** 传入 `:memory:` 可做测试。 */
  path?: string
}

export function initDatabase(dbPath: string, opts: InitDbOptions = {}): { db: Db; integrity: IntegrityResult } {
  const { recoverOnFailure = true } = opts
  const dir = dirname(dbPath)
  if (dbPath !== ':memory:' && !existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 })

  let integrity: IntegrityResult = 'ok'
  let db = openRaw(dbPath)

  // EDGE-L14：启动完整性校验
  try {
    const rows = db.pragma('integrity_check') as Array<{ integrity_check: string }>
    const first = rows[0]?.integrity_check
    if (first !== 'ok') {
      log.error('数据库完整性校验失败', { result: first })
      throw new Error(`integrity_check failed: ${first}`)
    }
  } catch (err) {
    log.error('数据库不可用，尝试恢复', { error: String(err) })
    try {
      db.close()
    } catch {
      /* ignore */
    }
    if (!recoverOnFailure || dbPath === ':memory:') throw err
    backupDatabase(dbPath, 'corrupt')
    for (const suffix of ['-wal', '-shm']) {
      const extra = `${dbPath}${suffix}`
      if (existsSync(extra)) {
        try {
          renameSync(extra, `${extra}.corrupt-${Date.now()}`)
        } catch {
          /* ignore */
        }
      }
    }
    db = openRaw(dbPath)
    integrity = 'recovered'
  }

  try {
    runMigrations(db)
  } catch (err) {
    log.error('迁移失败', { error: String(err) })
    try {
      db.close()
    } catch {
      /* ignore */
    }
    if (!recoverOnFailure || dbPath === ':memory:') throw err
    backupDatabase(dbPath, 'migration-failed')
    const fresh = join(dirname(dbPath), `tks.rebuilt-${Date.now()}.db`)
    db = openRaw(fresh)
    runMigrations(db)
    integrity = 'recovered'
    log.warn('已使用重建库继续运行', { fresh })
  }

  instance = db
  return { db, integrity }
}

export function getDb(): Db {
  if (!instance) throw new Error('database not initialized')
  return instance
}

export function closeDatabase(): void {
  if (!instance) return
  try {
    instance.pragma('wal_checkpoint(TRUNCATE)')
    instance.close()
  } catch (err) {
    log.warn('关闭数据库异常', { error: String(err) })
  }
  instance = null
}

/** 供测试使用。 */
export function __setDbForTest(db: Db): void {
  instance = db
}

export { MIGRATIONS }
