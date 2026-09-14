/**
 * 结构化日志（NFR-7）：JSON 行，记录 WS 连接生命周期、同步游标、错误码、traceId。
 * 滚动保留 7 天（FR-DSK-11），不上报外部服务（NFR-7）。
 * NFR-6：禁止输出 Token / 密码 / 图片 base64。
 */

import { createWriteStream, existsSync, readdirSync, statSync, unlinkSync, type WriteStream } from 'node:fs'
import { join } from 'node:path'
import { paths, redactForLog } from './paths'

export type LogLevel = 'debug' | 'info' | 'warn' | 'error'

const LEVEL_ORDER: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 }
const RETENTION_DAYS = 7

let stream: WriteStream | null = null
let currentDay = ''
let minLevel: LogLevel = (process.env.TKS_LOG_LEVEL as LogLevel) || 'info'

function dayStamp(d = new Date()): string {
  return `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`
}

function logFilePath(day = dayStamp()): string {
  return join(paths().logsDir, `tks-${day}.jsonl`)
}

/** 删除超过保留期的日志文件。 */
function pruneOldLogs(): void {
  const dir = paths().logsDir
  if (!existsSync(dir)) return
  const cutoff = Date.now() - RETENTION_DAYS * 24 * 3600 * 1000
  for (const name of readdirSync(dir)) {
    if (!/^tks-\d{8}\.jsonl$/.test(name)) continue
    try {
      const full = join(dir, name)
      if (statSync(full).mtimeMs < cutoff) unlinkSync(full)
    } catch {
      /* 清理失败不影响主流程 */
    }
  }
}

function ensureStream(): WriteStream | null {
  const day = dayStamp()
  if (stream && currentDay === day) return stream
  try {
    if (stream) stream.end()
    stream = createWriteStream(logFilePath(day), { flags: 'a', mode: 0o600 })
    currentDay = day
    pruneOldLogs()
    return stream
  } catch {
    return null
  }
}

function write(level: LogLevel, scope: string, message: string, fields?: Record<string, unknown>): void {
  if (LEVEL_ORDER[level] < LEVEL_ORDER[minLevel]) return
  const record = {
    ts: new Date().toISOString(),
    level,
    scope,
    msg: message,
    ...(fields ? (redactForLog(fields) as Record<string, unknown>) : {})
  }
  // 同时写 stdout 便于开发期观察
  const line = JSON.stringify(record)
  if (level === 'error') console.error(line)
  else if (level === 'warn') console.warn(line)
  else console.log(line)

  try {
    const s = ensureStream()
    s?.write(`${line}\n`)
  } catch {
    /* 磁盘不可写时静默降级为仅 stdout */
  }
}

export interface Logger {
  debug(message: string, fields?: Record<string, unknown>): void
  info(message: string, fields?: Record<string, unknown>): void
  warn(message: string, fields?: Record<string, unknown>): void
  error(message: string, fields?: Record<string, unknown>): void
  child(scope: string): Logger
}

export function createLogger(scope: string): Logger {
  return {
    debug: (m, f) => write('debug', scope, m, f),
    info: (m, f) => write('info', scope, m, f),
    warn: (m, f) => write('warn', scope, m, f),
    error: (m, f) => write('error', scope, m, f),
    child: (sub) => createLogger(`${scope}:${sub}`)
  }
}

export function setLogLevel(level: LogLevel): void {
  minLevel = level
}

export function currentLogFile(): string {
  return logFilePath()
}

export function closeLogger(): void {
  try {
    stream?.end()
  } catch {
    /* ignore */
  }
  stream = null
}
