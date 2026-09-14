/**
 * 桌面通知服务（§6.7 FR-NOTI-1..8、§8 FR-DSK-7/10）。
 *
 * 对标 Android 端 5 个通知渠道（`progress_updates` 为新增）：
 *   chat / greeting / reminder / error / progress
 * ⚠️ Android 实际只建了 4 个 `NotificationChannel`——**Bot 错误复用聊天渠道**；
 *    本端按 FR-NOTI-1 的「五类」实现，为错误单列一类配置项，但复用聊天渠道的展示语义。
 *
 * Linux 无「通知 tag」概念，同类替换通过**主动 close 上一条**实现（FR-NOTI-3）。
 */

import { Notification, nativeImage } from 'electron'
import { IPC } from '@shared/ipc'
import { createLogger } from '../../app/logger'
import { getSettings } from '../../app/config-store'
import { t } from '../../app/i18n'

const log = createLogger('notify')

export type NotificationKind = 'chat' | 'greeting' | 'reminder' | 'error' | 'progress'

/** FR-NOTI-3：同类通知固定 ID，替换展示而非堆叠（对标 Android 1002–1008）。 */
export const NOTIFICATION_IDS: Record<NotificationKind, number> = {
  chat: 1002,
  greeting: 1003,
  reminder: 1004,
  error: 1005,
  progress: 1006
}

/** Android 端另有独立的等级 / 断签 ID（1007 / 1008），此处保留语义等价。 */
export const NOTIFICATION_ID_LEVEL = 1007
export const NOTIFICATION_ID_STREAK = 1008

/** FR-NOTI-6：超长内容截断，完整内容在应用内可见。 */
const MAX_BODY = 160

export interface NotifyOptions {
  kind: NotificationKind
  title: string
  body: string
  /** XDG `critical` / `normal` / `low`。提醒与升级用 critical（FR-REM-5）。 */
  urgency?: 'low' | 'normal' | 'critical'
  /** FR-NOTI-4：点击后跳转的路由。 */
  route?: string
  /** 关联的本地消息 ID，用于滚动定位（FR-NOTI-4）。 */
  messageId?: string
  /** 固定 ID 覆盖（升级 / 断签使用独立 ID）。 */
  numericId?: number
  /** 是否允许在窗口聚焦时仍然展示（提醒类需要）。 */
  force?: boolean
}

export interface NotificationEvents {
  onActivate: (payload: { route?: string; messageId?: string; kind: NotificationKind }) => void
  /** FR-NOTI-8：「回复」动作 → 唤起窗口并聚焦输入框。 */
  onReplyAction: () => void
}

export class NotificationService {
  private active = new Map<NotificationKind, Notification>()
  private events: NotificationEvents
  private dndLoggedAt = 0

  constructor(events: NotificationEvents) {
    this.events = events
  }


  /** 判断 `HH:MM` 是否落在 [start, end) 区间内（支持跨午夜）。 */
  private inWindow(start: string, end: string): boolean {
    const parse = (value: string): number | null => {
      const m = /^(\d{1,2}):(\d{2})$/.exec((value ?? '').trim())
      if (!m) return null
      const h = Number(m[1])
      const min = Number(m[2])
      if (h > 23 || min > 59) return null
      return h * 60 + min
    }
    const s = parse(start)
    const e = parse(end)
    if (s === null || e === null || s === e) return false
    const now = new Date()
    const cur = now.getHours() * 60 + now.getMinutes()
    return s < e ? cur >= s && cur < e : cur >= s || cur < e
  }


  /** 由 window-manager 提供：窗口是否前台聚焦。 */
  private isWindowFocused: () => boolean = () => false
  setWindowFocusedProbe(probe: () => boolean): void {
    this.isWindowFocused = probe
  }

  notify(options: NotifyOptions): void {
    // NFR-7：每次通知请求都留下**单一结论行**，便于回答「为什么我没收到通知」。
    // 这也是集成自检用来确认 5 类渠道各自被真正触发的观测点。
    const outcome = this.resolveOutcome(options)
    if (outcome !== 'shown') {
      log.info('通知结果', { kind: options.kind, outcome })
      return
    }

    const body = options.body.length > MAX_BODY ? `${options.body.slice(0, MAX_BODY)}…` : options.body

    // FR-NOTI-3：同类替换，先关掉上一条
    this.close(options.kind)

    try {
      const notification = new Notification({
        title: options.title,
        body,
        urgency: options.urgency ?? 'normal',
        silent: false,
        // FR-NOTI-8 / FR-DSK-7：XDG 通知 actions（部分 DE 不支持，需优雅降级）
        // XDG 通知规范没有独立的动作文案本地化机制，按钮文本就是最终显示文本，
        // 因此这里直接用语言包（NFR-10：不硬编码界面文案）。
        actions:
          options.kind === 'chat' || options.kind === 'greeting'
            ? [
                { type: 'button', text: t('notify.action.reply') },
                { type: 'button', text: t('notify.action.ignore') }
              ]
            : undefined,
        // 提醒使用常驻，其余 10s 自动消失（对标 Android 10s 自动消失）
        timeoutType: options.kind === 'reminder' ? 'never' : 'default'
      })

      notification.on('click', () => {
        log.debug('通知被点击', { kind: options.kind, route: options.route })
        this.events.onActivate({ route: options.route, messageId: options.messageId, kind: options.kind })
      })
      notification.on('action', (_event, index) => {
        // index 0 = Reply，1 = Ignore
        if (index === 0) {
          this.events.onReplyAction()
        } else {
          this.close(options.kind)
        }
      })
      notification.on('close', () => {
        this.active.delete(options.kind)
      })
      notification.show()
      this.active.set(options.kind, notification)
      log.info('通知结果', {
        kind: options.kind,
        outcome: 'shown',
        numericId: options.numericId ?? NOTIFICATION_IDS[options.kind]
      })
    } catch (err) {
      log.warn('通知结果', { kind: options.kind, outcome: 'error', error: String(err) })
    }
  }

  /**
   * 判定本次通知的最终去向（不产生副作用，便于日志与测试）。
   *
   * 顺序即优先级：分类开关 → 勿扰时段 → 窗口聚焦抑制（FR-NOTI-2）→ 系统支持。
   */
  private resolveOutcome(options: NotifyOptions): 'shown' | 'suppressed-category' | 'suppressed-dnd' | 'suppressed-focus' | 'unsupported' {
    const settings = getSettings()
    if (!settings.notifications[options.kind]) return 'suppressed-category'

    const dnd = settings.doNotDisturb
    if (dnd.enabled && this.inWindow(dnd.start, dnd.end)) {
      const now = Date.now()
      if (now - this.dndLoggedAt > 60_000) {
        this.dndLoggedAt = now
        log.info('勿扰时段内，通知已抑制', { start: dnd.start, end: dnd.end })
      }
      return 'suppressed-dnd'
    }

    // FR-NOTI-2：聊天 / 问候 / 错误类在窗口前台聚焦时不弹
    const focusSensitive = options.kind === 'chat' || options.kind === 'greeting' || options.kind === 'error'
    if (focusSensitive && !options.force && this.isWindowFocused()) return 'suppressed-focus'

    if (!Notification.isSupported()) return 'unsupported'
    return 'shown'
  }

  /** FR-NOTI-5：应用回到前台时清除所有消息类通知（对标 `AppNotifier.dismissAll()`）。 */
  dismissAll(): void {
    for (const kind of ['chat', 'greeting', 'error'] as NotificationKind[]) {
      this.close(kind)
    }
  }

  dismissAllKinds(): void {
    for (const kind of [...this.active.keys()]) this.close(kind)
  }

  private close(kind: NotificationKind): void {
    const existing = this.active.get(kind)
    if (!existing) return
    try {
      existing.close()
    } catch {
      /* ignore */
    }
    this.active.delete(kind)
  }

  /** FR-NOTI-4：桌面通知点击 → 唤起并聚焦主窗口。 */
  static iconFromPath(path: string): Electron.NativeImage | undefined {
    try {
      const img = nativeImage.createFromPath(path)
      return img.isEmpty() ? undefined : img
    } catch {
      return undefined
    }
  }
}

export { IPC }
