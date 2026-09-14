/**
 * 消息与附件仓储（§9.1 / §9.2）。
 *
 * 关键约束：
 * - FR-DB-1：更新消息行**必须**用 `UPDATE`，禁止 `INSERT OR REPLACE`
 *   （`REPLACE` 会先 DELETE 再 INSERT，外键 `ON DELETE CASCADE` 会连带删掉附件行）。
 * - FR-CHAT-6 / FR-SYNC-5：Bot 消息按 `\n` 拆分为多条，主键 `{messageId}_{index}`，
 *   timestamp 为 `{finalTimestamp}+{index}`；实时链路与历史同步共用**同一**拆分函数。
 * - FR-INT-12 / EDGE-L21：按拆分后 `messageId` 去重，靠 `delivered_bot_messages` 表。
 */

import { randomUUID } from 'node:crypto'
import type { ChatAttachment, ChatMessage, ChatRole, MessageStatus } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { getDb } from '../db'

/* -------------------------------------------------------------------------- */
/* 行映射                                                                       */
/* -------------------------------------------------------------------------- */

interface MessageRow {
  message_id: string
  session_id: string
  role: string
  message_type: string
  content_type: string
  model_provider: string | null
  content: string
  status: string
  timestamp: number
  error_code: string | null
}

interface AttachmentRow {
  attachment_id: string
  message_id: string
  session_id: string
  mime_type: string
  local_path: string
  file_size: number
  width: number | null
  height: number | null
  upload_state: string
  timestamp: number
}

function toMessage(row: MessageRow): ChatMessage {
  return {
    messageId: row.message_id,
    sessionId: row.session_id,
    role: row.role as ChatRole,
    messageType: row.message_type === 'image' ? 'image' : 'text',
    contentType: (row.content_type as ChatMessage['contentType']) ?? 'text',
    modelProvider: row.model_provider,
    content: row.content ?? '',
    status: row.status as MessageStatus,
    timestamp: row.timestamp,
    errorCode: row.error_code
  }
}

function toAttachment(row: AttachmentRow): ChatAttachment {
  return {
    attachmentId: row.attachment_id,
    messageId: row.message_id,
    sessionId: row.session_id,
    mimeType: row.mime_type,
    localPath: row.local_path,
    fileSize: row.file_size,
    width: row.width,
    height: row.height,
    uploadState: row.upload_state,
    timestamp: row.timestamp
  }
}

/* -------------------------------------------------------------------------- */
/* 多气泡拆分（FR-CHAT-6 / FR-SYNC-5，全链路唯一实现）                            */
/* -------------------------------------------------------------------------- */

/**
 * 把 Bot 最终内容按 `\n` 拆分为多条气泡，过滤空行。
 * `messageId` = `{finalMessageId}_{index}`，`timestamp` = `{finalTimestamp}+{index}`。
 *
 * ⚠️ 实时流式 `done` 链路与历史同步链路**必须**都调用本函数，
 *    否则同一条消息会以两种形态重复入库（FR-SYNC-5）。
 */
export function splitBotContent(
  finalMessageId: string,
  content: string,
  timestamp: number
): Array<{ messageId: string; content: string; timestamp: number }> {
  const lines = (content ?? '')
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)

  if (lines.length === 0) return []
  // 单行时保持原始 messageId，避免与服务端/其他端不一致
  if (lines.length === 1) {
    return [{ messageId: finalMessageId, content: lines[0], timestamp }]
  }
  return lines.map((line, index) => ({
    messageId: `${finalMessageId}_${index}`,
    content: line,
    timestamp: timestamp + index
  }))
}

/** FR-SYNC-9：剥离历史中的 `【MM-DD HH:MM】` 前缀。 */
export function stripHistoryTimestampPrefix(content: string): string {
  return (content ?? '').replace(/^\s*【\d{2}-\d{2}\s+\d{2}:\d{2}】\s*/u, '')
}

/** FR-SYNC-8：服务端每日 06:00 的分隔标记不应渲染为普通气泡。 */
export function isHistorySeparator(content: string): boolean {
  return (content ?? '').trim() === PROTOCOL.HISTORY_SEPARATOR
}

/* -------------------------------------------------------------------------- */
/* 写入                                                                         */
/* -------------------------------------------------------------------------- */

export interface UpsertMessageInput {
  messageId: string
  sessionId: string
  role: ChatRole
  messageType?: 'text' | 'image'
  contentType?: 'text' | 'image' | 'mixed'
  modelProvider?: string | null
  content: string
  status: MessageStatus
  timestamp: number
  errorCode?: string | null
}

/** `upsertMessage` 的结果。区分「新建」「就地更新」「无变化」三态。 */
export type UpsertOutcome = 'inserted' | 'updated' | 'unchanged'

/**
 * 幂等插入：已存在则**仅**在给定字段确有变化时 `UPDATE`（FR-DB-1，绝不 REPLACE）。
 *
 * ⚠️ 返回值必须区分 `updated` 与 `unchanged`：历史同步把一条 `error` 消息修复成
 * `sent` 时走的是 UPDATE 分支，若统一返回「非新建」，调用方就无法得知需要刷新 UI，
 * 于是消息在库里已经修好、界面上却一直显示「发送失败」（问题 3）。
 */
export function upsertMessage(input: UpsertMessageInput): UpsertOutcome {
  const db = getDb()
  const existing = db
    .prepare('SELECT message_id, status, content FROM chat_messages WHERE message_id = ?')
    .get(input.messageId) as { message_id: string; status: string; content: string } | undefined

  if (existing) {
    // 已经是终态且内容一致 → 不写库（幂等，避免 trigger 抖动）
    if (existing.status === input.status && existing.content === input.content) return 'unchanged'
    db.prepare(
      `UPDATE chat_messages
          SET status = ?, content = ?, error_code = ?, model_provider = ?,
              content_type = ?, message_type = ?
        WHERE message_id = ?`
    ).run(
      input.status,
      input.content,
      input.errorCode ?? null,
      input.modelProvider ?? null,
      input.contentType ?? 'text',
      input.messageType ?? 'text',
      input.messageId
    )
    return 'updated'
  }

  db.prepare(
    `INSERT INTO chat_messages
       (message_id, session_id, role, message_type, content_type,
        model_provider, content, status, timestamp, error_code)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).run(
    input.messageId,
    input.sessionId,
    input.role,
    input.messageType ?? 'text',
    input.contentType ?? 'text',
    input.modelProvider ?? null,
    input.content,
    input.status,
    input.timestamp,
    input.errorCode ?? null
  )
  return 'inserted'
}

/** 仅更新状态（不触碰其它列，避免无谓的 FTS 触发器开销）。 */
export function updateMessageStatus(messageId: string, status: MessageStatus, errorCode?: string | null): void {
  const db = getDb()
  db.prepare('UPDATE chat_messages SET status = ?, error_code = ? WHERE message_id = ?').run(
    status,
    errorCode ?? null,
    messageId
  )
}

export function updateMessageContent(messageId: string, content: string): void {
  const db = getDb()
  db.prepare('UPDATE chat_messages SET content = ? WHERE message_id = ?').run(content, messageId)
}

export function deleteMessage(messageId: string): void {
  const db = getDb()
  // 外键 CASCADE 会一并删除附件行（§9.2）
  db.prepare('DELETE FROM chat_messages WHERE message_id = ?').run(messageId)
}

/* -------------------------------------------------------------------------- */
/* 流式占位消息                                                                  */
/* -------------------------------------------------------------------------- */

export function pendingMessageId(requestId: string): string {
  return `pending_${requestId}`
}

export function createStreamingPlaceholder(input: {
  requestId: string
  sessionId: string
  contentType: 'text' | 'image' | 'mixed'
  modelProvider: string
  timestamp: number
}): string {
  const id = pendingMessageId(input.requestId)
  upsertMessage({
    messageId: id,
    sessionId: input.sessionId,
    role: 'bot',
    messageType: 'text',
    contentType: input.contentType,
    modelProvider: input.modelProvider,
    content: '',
    status: 'streaming',
    timestamp: input.timestamp
  })
  return id
}

export function appendStreamingDelta(requestId: string, delta: string): void {
  const db = getDb()
  db.prepare(
    `UPDATE chat_messages
        SET content = content || ?, status = 'streaming'
      WHERE message_id = ?`
  ).run(delta, pendingMessageId(requestId))
}

export function deletePendingMessage(requestId: string): void {
  deleteMessage(pendingMessageId(requestId))
}

/** EDGE-L7：启动时规整孤儿 `streaming` 消息——有内容转 `received`，无内容删除。 */
export function normalizeStreamingMessages(): { promoted: number; deleted: number } {
  const db = getDb()
  const tx = db.transaction(() => {
    const deleted = db
      .prepare(`DELETE FROM chat_messages WHERE status = 'streaming' AND TRIM(content) = ''`)
      .run().changes
    const promoted = db.prepare(`UPDATE chat_messages SET status = 'received' WHERE status = 'streaming'`).run().changes
    return { promoted, deleted }
  })
  const result = tx() as { promoted: number; deleted: number }
  return result
}

/** FR-CHAT-10：清理历史遗留的空 Bot 消息（服务端已实现重采样与 `……` 占位，客户端不得落空内容）。 */
export function deleteBlankBotMessages(): number {
  const db = getDb()
  return db.prepare(`DELETE FROM chat_messages WHERE role = 'bot' AND TRIM(content) = ''`).run().changes
}

/* -------------------------------------------------------------------------- */
/* 查询                                                                         */
/* -------------------------------------------------------------------------- */

interface ListOptions {
  sessionId?: string
  limit: number
  beforeTimestamp?: number
}

export function listMessages(opts: ListOptions): ChatMessage[] {
  const db = getDb()
  const sessionId = opts.sessionId ?? PROTOCOL.DEFAULT_SESSION_ID
  // FR-SYNC-8：分隔标记不渲染为普通气泡，查询层直接过滤
  const rows = opts.beforeTimestamp
    ? (db
        .prepare(
          `SELECT * FROM chat_messages
            WHERE session_id = ?
              AND timestamp < ?
              AND TRIM(content) <> ''
              AND content <> ?
            ORDER BY timestamp DESC
            LIMIT ?`
        )
        .all(sessionId, opts.beforeTimestamp, PROTOCOL.HISTORY_SEPARATOR, opts.limit) as MessageRow[])
    : (db
        .prepare(
          `SELECT * FROM chat_messages
            WHERE session_id = ?
              AND TRIM(content) <> ''
              AND content <> ?
            ORDER BY timestamp DESC
            LIMIT ?`
        )
        .all(sessionId, PROTOCOL.HISTORY_SEPARATOR, opts.limit) as MessageRow[])

  const messages = rows.map(toMessage).reverse()
  return attachAttachments(messages)
}

export function getMessage(messageId: string): ChatMessage | null {
  const db = getDb()
  const row = db.prepare('SELECT * FROM chat_messages WHERE message_id = ?').get(messageId) as MessageRow | undefined
  if (!row) return null
  return attachAttachments([toMessage(row)])[0]
}

/** 本地最大时间戳，作为增量同步 `since`（FR-SYNC-3）。 */
export function maxTimestamp(sessionId = PROTOCOL.DEFAULT_SESSION_ID): number {
  const db = getDb()
  const row = db
    .prepare('SELECT MAX(timestamp) AS ts FROM chat_messages WHERE session_id = ?')
    .get(sessionId) as { ts: number | null } | undefined
  return row?.ts ?? 0
}

export function countMessages(sessionId = PROTOCOL.DEFAULT_SESSION_ID): number {
  const db = getDb()
  const row = db
    .prepare('SELECT COUNT(*) AS n FROM chat_messages WHERE session_id = ?')
    .get(sessionId) as { n: number }
  return row.n
}

/**
 * FR-CHAT-17：本地消息全文搜索。
 *
 * ⚠️ 分词器选择（实测结论，见迁移 v5 的说明）：
 *   - `unicode61`（`chat_messages_fts`）：把连续中文当作**一个 token**，
 *     搜索「递归函数」无法命中「今天写了一个很长的递归函数」→ 中文子串搜索会静默返回空。
 *   - `trigram`（`chat_messages_fts_tri`）：按 3 字符滑窗建索引，
 *     中文片段与拉丁子串都能命中；但查询词必须 ≥3 个字符。
 *
 * 因此路由策略：
 *   ① 查询词 ≥3 字符 → 走 trigram 索引（中文与拉丁子串均可用）
 *   ② 查询词 <3 字符 → 走 unicode61 索引（整词/整串命中）
 *   ③ 上述均无结果 → 回退 LIKE 子串匹配（保证「永不返回假空」）
 *
 * 三者都不抛错：搜索是辅助功能，绝不能因为查询语法问题中断界面。
 */
export function searchMessages(keyword: string, limit = 100): ChatMessage[] {
  const db = getDb()
  const term = (keyword ?? '').trim()
  if (!term) return []

  // FTS5 语法转义：整体加引号，避免用户输入的运算符导致语法错误
  const escaped = `"${term.replace(/"/g, '""')}"`
  const safeLimit = Math.max(1, Math.min(limit, 500))

  const viaIndex = (table: string): MessageRow[] =>
    db
      .prepare(
        `SELECT m.* FROM ${table} f
           JOIN chat_messages m ON m.message_id = f.message_id
          WHERE ${table} MATCH ?
          ORDER BY m.timestamp DESC
          LIMIT ?`
      )
      .all(escaped, safeLimit) as MessageRow[]

  const viaLike = (): MessageRow[] =>
    db
      .prepare(
        `SELECT * FROM chat_messages
          WHERE content LIKE ? ESCAPE '\\'
          ORDER BY timestamp DESC LIMIT ?`
      )
      .all(`%${term.replace(/[\\%_]/g, (ch) => `\\${ch}`)}%`, safeLimit) as MessageRow[]

  // trigram 需要至少 3 个字符
  const useTrigram = [...term].length >= 3
  let rows: MessageRow[] = []

  try {
    rows = useTrigram ? viaIndex('chat_messages_fts_tri') : viaIndex('chat_messages_fts')
  } catch (err) {
    logDebug('FTS 查询失败，回退 LIKE', { error: String(err), term })
    rows = []
  }

  // 无结果时回退 LIKE —— 覆盖 1~2 字中文查询与 trigram 未命中场景
  if (rows.length === 0) {
    try {
      rows = viaLike()
    } catch (err) {
      logDebug('LIKE 回退亦失败', { error: String(err) })
      return []
    }
  }

  return attachAttachments(rows.map(toMessage))
}

function logDebug(message: string, fields?: Record<string, unknown>): void {
  // 避免在仓储层引入完整 logger 依赖：仅在需要时惰性输出
  if (process.env.TKS_LOG_LEVEL === 'debug') {
    // eslint-disable-next-line no-console
    console.debug(JSON.stringify({ scope: 'search', message, ...fields }))
  }
}

/* -------------------------------------------------------------------------- */
/* 清空会话（FR-CHAT-12）                                                        */
/* -------------------------------------------------------------------------- */

/**
 * 清空本地消息表，并把同步游标推到 `now`，防止立刻被历史补拉回灌。
 *
 * ⚠️ FR-CHAT-12：设置页「清空本地会话」与聊天页「清空会话」**必须共用本函数**，
 *    Android 端因两条路径实现不一致导致清空后历史立刻被拉回。
 */
export function clearConversationSession(opts: { advanceCursor: boolean }): { deleted: number; cursor: number } {
  const db = getDb()
  const now = Date.now()
  const tx = db.transaction(() => {
    db.prepare('DELETE FROM chat_attachments').run()
    const deleted = db.prepare('DELETE FROM chat_messages').run().changes
    db.prepare('DELETE FROM delivered_bot_messages').run()
    if (opts.advanceCursor) {
      setCursor('chat_history', now)
    }
    return deleted
  })
  const deleted = tx() as number
  return { deleted, cursor: opts.advanceCursor ? now : getCursor('chat_history') }
}

/* -------------------------------------------------------------------------- */
/* 同步游标                                                                     */
/* -------------------------------------------------------------------------- */

export function getCursor(key: string): number {
  const db = getDb()
  const row = db.prepare('SELECT value FROM sync_cursors WHERE key = ?').get(key) as { value: number } | undefined
  return row?.value ?? 0
}

export function setCursor(key: string, value: number): void {
  const db = getDb()
  db.prepare(
    `INSERT INTO sync_cursors (key, value) VALUES (?, ?)
     ON CONFLICT (key) DO UPDATE SET value = excluded.value`
  ).run(key, value)
}

/* -------------------------------------------------------------------------- */
/* 已投递 Bot 消息去重（FR-INT-12 / EDGE-L21）                                    */
/* -------------------------------------------------------------------------- */

export function markDelivered(messageIds: string[]): void {
  if (messageIds.length === 0) return
  const db = getDb()
  const stmt = db.prepare('INSERT OR IGNORE INTO delivered_bot_messages (message_id, timestamp) VALUES (?, ?)')
  const now = Date.now()
  const tx = db.transaction(() => {
    for (const id of messageIds) stmt.run(id, now)
  })
  tx()
}

export function isDelivered(messageId: string): boolean {
  const db = getDb()
  const row = db.prepare('SELECT 1 AS x FROM delivered_bot_messages WHERE message_id = ?').get(messageId)
  return !!row
}

/** 清理过期的去重记录（保留 30 天），避免无限增长。 */
export function pruneDelivered(olderThanMs = 30 * 24 * 3600 * 1000): number {
  const db = getDb()
  return db.prepare('DELETE FROM delivered_bot_messages WHERE timestamp < ?').run(Date.now() - olderThanMs).changes
}

/* -------------------------------------------------------------------------- */
/* 附件（§9.2）                                                                 */
/* -------------------------------------------------------------------------- */

export interface CreateAttachmentInput {
  /** `null` 表示草稿附件（尚未发送，FR-IMG-3 / FR-IMG-5）。 */
  messageId: string | null
  sessionId: string
  mimeType: string
  localPath: string
  fileSize: number
  width?: number | null
  height?: number | null
}

export function insertAttachment(input: CreateAttachmentInput): ChatAttachment {
  const db = getDb()
  const attachmentId = randomUUID()
  const timestamp = Date.now()
  db.prepare(
    `INSERT INTO chat_attachments
       (attachment_id, message_id, session_id, mime_type, local_path,
        file_size, width, height, upload_state, timestamp)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'local', ?)`
  ).run(
    attachmentId,
    input.messageId,
    input.sessionId,
    input.mimeType,
    input.localPath,
    input.fileSize,
    input.width ?? null,
    input.height ?? null,
    timestamp
  )
  return {
    attachmentId,
    messageId: input.messageId ?? '',
    sessionId: input.sessionId,
    mimeType: input.mimeType,
    localPath: input.localPath,
    fileSize: input.fileSize,
    width: input.width ?? null,
    height: input.height ?? null,
    uploadState: 'local',
    timestamp
  }
}

/** 当前草稿附件列表（`message_id IS NULL`）。 */
export function listDraftAttachments(): ChatAttachment[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM chat_attachments WHERE message_id IS NULL ORDER BY timestamp ASC')
    .all() as AttachmentRow[]
  return rows.map(toAttachment)
}

/** 清理所有草稿附件（发送成功或用户手动清除时调用）。 */
export function deleteDraftAttachments(): number {
  const db = getDb()
  return db.prepare('DELETE FROM chat_attachments WHERE message_id IS NULL').run().changes
}

export function listAttachmentsByMessageIds(messageIds: string[]): ChatAttachment[] {
  if (messageIds.length === 0) return []
  const db = getDb()
  const placeholders = messageIds.map(() => '?').join(',')
  const rows = db
    .prepare(`SELECT * FROM chat_attachments WHERE message_id IN (${placeholders})`)
    .all(...messageIds) as AttachmentRow[]
  return rows.map(toAttachment)
}

export function listAttachmentsByMessage(messageId: string): ChatAttachment[] {
  return listAttachmentsByMessageIds([messageId])
}

export function getAttachment(attachmentId: string): ChatAttachment | null {
  const db = getDb()
  const row = db.prepare('SELECT * FROM chat_attachments WHERE attachment_id = ?').get(attachmentId) as
    | AttachmentRow
    | undefined
  return row ? toAttachment(row) : null
}

export function deleteAttachment(attachmentId: string): void {
  const db = getDb()
  db.prepare('DELETE FROM chat_attachments WHERE attachment_id = ?').run(attachmentId)
}

/** 把草稿附件挂到真实消息上（发送成功时调用）。 */
export function rebindAttachments(attachmentIds: string[], messageId: string): void {
  if (attachmentIds.length === 0) return
  const db = getDb()
  const placeholders = attachmentIds.map(() => '?').join(',')
  db.prepare(`UPDATE chat_attachments SET message_id = ? WHERE attachment_id IN (${placeholders})`).run(
    messageId,
    ...attachmentIds
  )
}

/** 清理指向不存在消息的孤儿附件（正常流程不应出现，作为兜底）。**不会**动草稿（`message_id IS NULL`）。 */
export function pruneOrphanAttachments(): number {
  const db = getDb()
  return db
    .prepare(
      `DELETE FROM chat_attachments
        WHERE message_id IS NOT NULL
          AND message_id NOT IN (SELECT message_id FROM chat_messages)`
    )
    .run().changes
}

export function attachmentUsage(): { count: number; bytes: number } {
  const db = getDb()
  const row = db.prepare('SELECT COUNT(*) AS n, COALESCE(SUM(file_size), 0) AS b FROM chat_attachments').get() as {
    n: number
    b: number
  }
  return { count: row.n, bytes: row.b }
}

/** 列出早于给定时刻的附件（EDGE-L15 清理入口）。 */
export function listAttachmentsOlderThan(cutoff: number, limit = 500): ChatAttachment[] {
  const db = getDb()
  const rows = db
    .prepare('SELECT * FROM chat_attachments WHERE timestamp < ? ORDER BY timestamp ASC LIMIT ?')
    .all(cutoff, limit) as AttachmentRow[]
  return rows.map(toAttachment)
}

/** 所有附件的磁盘路径（用于孤儿文件扫描）。 */
export function listAllAttachmentPaths(): string[] {
  const db = getDb()
  const rows = db.prepare('SELECT local_path FROM chat_attachments').all() as Array<{ local_path: string }>
  return rows.map((r) => r.local_path)
}

function attachAttachments(messages: ChatMessage[]): ChatMessage[] {
  if (messages.length === 0) return messages
  const map = new Map<string, ChatAttachment[]>()
  for (const att of listAttachmentsByMessageIds(messages.map((m) => m.messageId))) {
    const list = map.get(att.messageId) ?? []
    list.push(att)
    map.set(att.messageId, list)
  }
  return messages.map((m) => ({ ...m, attachments: map.get(m.messageId) ?? [] }))
}

export function listDeliveredCount(): number {
  const db = getDb()
  const row = db.prepare('SELECT COUNT(*) AS n FROM delivered_bot_messages').get() as { n: number }
  return row.n
}
