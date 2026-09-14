/**
 * 格式化工具。EDGE-L13：内部一律用 UTC 毫秒，仅在渲染时转本地时区。
 */

/** 消息时间：同一天只显示 HH:MM。 */
export function formatTime(ms: number): string {
  const d = new Date(ms)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

export function formatDateTime(ms: number): string {
  const d = new Date(ms)
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function formatMonthDay(ms: number): string {
  const d = new Date(ms)
  return `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** FR-CHAT-13：同一分钟内的连续消息合并展示时间。 */
export function sameMinute(a: number, b: number): boolean {
  return Math.floor(a / 60000) === Math.floor(b / 60000)
}

export function isSameLocalDay(a: number, b: number): boolean {
  const da = new Date(a)
  const db = new Date(b)
  return (
    da.getFullYear() === db.getFullYear() && da.getMonth() === db.getMonth() && da.getDate() === db.getDate()
  )
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i += 1
  }
  return `${value >= 10 || i === 0 ? Math.round(value) : value.toFixed(1)} ${units[i]}`
}

/**
 * FR-PROG-3：所有「自然日」展示以**服务端返回的日期字符串**为准。
 * 这里只做「与今天相差几天」的展示辅助，不参与业务判定。
 */
export function daysBetweenDateStrings(a: string, b: string): number | null {
  const pa = Date.parse(`${a}T00:00:00Z`)
  const pb = Date.parse(`${b}T00:00:00Z`)
  if (Number.isNaN(pa) || Number.isNaN(pb)) return null
  return Math.round((pb - pa) / 86400000)
}

/** 把服务端 `YYYY-MM-DD` 渲染为 `M月D日`（不重新计算日期本身）。 */
export function formatServerDate(date: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date ?? '')
  if (!m) return date ?? ''
  return `${Number(m[2])}月${Number(m[3])}日`
}

/** 星期几（本地）。 */
export function weekdayShort(ms: number): string {
  const names = ['日', '一', '二', '三', '四', '五', '六']
  return names[new Date(ms).getDay()]
}

export function truncate(text: string, max: number): string {
  if (!text) return ''
  return text.length > max ? `${text.slice(0, max)}…` : text
}
