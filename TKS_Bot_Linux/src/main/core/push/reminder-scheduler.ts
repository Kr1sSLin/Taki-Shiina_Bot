/**
 * 提醒调度（§6.6 FR-REM-1..7、§9.5）。
 *
 * Android 靠 `WorkManager` + `ReminderWorker` 做系统级持久化；
 * Electron **无等价能力**，因此必须自行落库（`reminders` 表）并在启动时重新装载。
 *
 * - FR-REM-1：只消费服务端已解析的 `timerInstruction`，
 *   客户端**不**解析 `[[TIMER:HH:MM|text]]` 原始标记（那是服务端 `parse_timer_instruction` 的职责）
 * - FR-REM-2：取今日该时刻；已过则顺延至明日
 * - FR-REM-3：去重键已存在则**保留原计划**（`ExistingWorkPolicy.KEEP` 语义）
 * - FR-REM-4：重启后重载未触发的提醒；已过期未触发的按「超 30 分钟丢弃、否则立即触发」
 * - FR-REM-5：高优先级通知，标题「消息提醒」，正文「提醒时间：{target}\n{text}」
 * - FR-REM-7：系统休眠跨过提醒时刻时，唤醒后立即补发
 */

import { IPC } from '@shared/ipc'
import { PROTOCOL } from '@shared/levels'
import type { ReminderRecord } from '@shared/protocol'
import { bus } from '../../app/bus'
import { t } from '../../app/i18n'
import { createLogger } from '../../app/logger'
import {
  cancelReminder,
  insertReminderKeep,
  listPendingReminders,
  listReminders,
  pruneReminders,
  setReminderStatus
} from '../database/repositories/reminder-repository'
import type { NotificationService } from './notification-service'

const log = createLogger('reminder')

/** 单次 `setTimeout` 的安全上限（超过 2^31-1 ms 会溢出）。 */
const MAX_TIMEOUT_MS = 2_147_483_000

export interface ReminderEvents {
  onFired: (record: ReminderRecord) => void
}

export class ReminderScheduler {
  private notify: NotificationService
  private events: ReminderEvents
  private timers = new Map<string, NodeJS.Timeout>()
  private started = false

  constructor(opts: { notify: NotificationService; events: ReminderEvents }) {
    this.notify = opts.notify
    this.events = opts.events
  }

  /**
   * FR-REM-2：计算目标触发时刻。
   * 取「业务/本地今日该时刻」，若已过则顺延至明日。
   *
   * 注意：这里用**本地时区**是刻意的——提醒是用户对自己说的「两点叫我睡觉」，
   * 语义是用户本地的两点。积分/等级的「自然日」才必须用服务端日期（FR-PROG-3），
   * 两者不冲突。
   */
  computeFireAt(target: string, from = Date.now()): number {
    const match = /^(\d{1,2}):(\d{2})$/.exec((target ?? '').trim())
    if (!match) {
      log.warn('提醒时间格式非法，回退为 1 小时后', { target })
      return from + 3600_000
    }
    const hour = Math.min(23, Number(match[1]))
    const minute = Math.min(59, Number(match[2]))

    const base = new Date(from)
    const candidate = new Date(base)
    candidate.setHours(hour, minute, 0, 0)
    if (candidate.getTime() <= from) {
      // 已过 → 顺延至明日
      candidate.setDate(candidate.getDate() + 1)
    }
    return candidate.getTime()
  }

  /** 启动：装载持久化提醒 + 处理过期项（FR-REM-4）。 */
  start(): void {
    if (this.started) return
    this.started = true

    const now = Date.now()
    const pending = listPendingReminders()
    log.info('装载已排程提醒', { count: pending.length })

    let fired = 0
    let discarded = 0

    for (const record of pending) {
      const overdue = record.fireAt - now
      if (overdue > 0) {
        this.arm(record)
        continue
      }
      // 已过期未触发
      const lateBy = now - record.fireAt
      if (lateBy > PROTOCOL.REMINDER_STALE_MS) {
        // 超过 30 分钟 → 丢弃
        setReminderStatus(record.reminderId, 'cancelled')
        discarded += 1
        log.info('丢弃过期提醒（超过 30 分钟）', { reminderId: record.reminderId, lateMinutes: Math.round(lateBy / 60000) })
      } else {
        // 30 分钟内 → 立即触发
        this.fire(record)
        fired += 1
      }
    }

    if (fired || discarded) log.info('启动提醒规整完成', { fired, discarded, armed: this.timers.size })
    pruneReminders()
    this.emitChanged()
  }

  /** 新增提醒（FR-REM-3：同键保留原计划）。 */
  schedule(input: { reminderId: string; targetTime: string; fireAt: number; text: string }): boolean {
    const { created, record } = insertReminderKeep(input)
    if (!created) {
      log.info('提醒已存在，保留原计划', { reminderId: input.reminderId, status: record?.status })
      return false
    }
    if (record) this.arm(record)
    this.emitChanged()
    return true
  }

  /** 由 `timerInstruction` 直接排程（chat-service 调用）。 */
  scheduleFromInstruction(instruction: { target: string; text: string }, requestIds: string[]): boolean {
    const fireAt = this.computeFireAt(instruction.target)
    const reminderId = `reminder_${requestIds.join('_')}_${instruction.target}_${hash(instruction.text)}`
    return this.schedule({ reminderId, targetTime: instruction.target, fireAt, text: instruction.text })
  }

  cancel(reminderId: string): boolean {
    const ok = cancelReminder(reminderId)
    const timer = this.timers.get(reminderId)
    if (timer) {
      clearTimeout(timer)
      this.timers.delete(reminderId)
    }
    if (ok) this.emitChanged()
    return ok
  }

  list(): ReminderRecord[] {
    return listReminders()
  }

  /** FR-REM-7：休眠唤醒后重新校验，错过的提醒立即补发。 */
  revalidateOnResume(): void {
    const now = Date.now()
    const pending = listPendingReminders()
    let fired = 0
    for (const record of pending) {
      if (record.fireAt <= now) {
        const lateBy = now - record.fireAt
        if (lateBy > PROTOCOL.REMINDER_STALE_MS) {
          setReminderStatus(record.reminderId, 'cancelled')
          log.info('唤醒后丢弃过期提醒', { reminderId: record.reminderId })
        } else {
          this.fire(record)
          fired += 1
        }
      } else if (!this.timers.has(record.reminderId)) {
        // 定时器可能因休眠/时钟变化失效 → 重新装载
        this.arm(record)
      }
    }
    if (fired) {
      log.info('唤醒后补发提醒', { count: fired })
      this.emitChanged()
    }
  }

  dispose(): void {
    for (const timer of this.timers.values()) clearTimeout(timer)
    this.timers.clear()
    this.started = false
  }

  /* --------------------------------- 内部 --------------------------------- */

  private arm(record: ReminderRecord): void {
    const existing = this.timers.get(record.reminderId)
    if (existing) clearTimeout(existing)

    const delay = record.fireAt - Date.now()
    if (delay <= 0) {
      this.fire(record)
      return
    }

    // 超过 setTimeout 安全上限时先挂一个长定时器，到期后重新计算
    const effectiveDelay = Math.min(delay, MAX_TIMEOUT_MS)
    const timer = setTimeout(() => {
      if (Date.now() >= record.fireAt) {
        this.fire(record)
      } else {
        this.arm(record)
      }
    }, effectiveDelay)
    timer.unref?.()
    this.timers.set(record.reminderId, timer)
    log.debug('提醒已装载', { reminderId: record.reminderId, delayMs: delay })
  }

  /** FR-REM-5：高优先级桌面通知。 */
  private fire(record: ReminderRecord): void {
    const timer = this.timers.get(record.reminderId)
    if (timer) clearTimeout(timer)
    this.timers.delete(record.reminderId)

    setReminderStatus(record.reminderId, 'fired')

    this.notify.notify({
      kind: 'reminder',
      title: t('notify.reminder.title'),
      body: t('notify.reminder.body', { target: record.targetTime, text: record.text }),
      urgency: 'critical',
      route: '/chat',
      force: true
    })

    log.info('提醒已触发', { reminderId: record.reminderId, target: record.targetTime })
    this.events.onFired(record)
    this.emitChanged()
  }

  private emitChanged(): void {
    bus.send(IPC.evtRemindersChanged, { reminders: listPendingReminders() })
  }
}

function hash(text: string): string {
  let h = 0
  for (let i = 0; i < text.length; i += 1) {
    h = (h << 5) - h + text.charCodeAt(i)
    h |= 0
  }
  return Math.abs(h).toString(36)
}
