/**
 * 连接管理器（§10.1 连接状态机；FR-CONN-1..9）。
 *
 * 对标 Android 前台服务 `WebSocketService`：连接生命周期与**主进程**绑定，
 * 渲染进程关闭（窗口隐藏到托盘）时**不得**断开（FR-ARCH-1 / NFR-2）。
 *
 * 状态机（§10.1）：
 *   unauthenticated → connecting → connected
 *   connecting → reconnecting（握手失败，非 401/403）
 *   connecting|connected → refreshing（握手 401/403 或收到 auth.expired）
 *   connected → reconnecting（onClose / 心跳发送失败）
 *   reconnecting → connecting（退避结束 2^n，上限 60s）
 *   reconnecting → degraded（重连达 15 次上限，开放 REST 降级通道 FR-CHAT-9）
 *   refreshing → connecting（刷新成功）/ unauthenticated（刷新失败）
 *   degraded → connecting（手动重连 / 系统唤醒 / 网络恢复）
 */

import { EventEmitter } from 'node:events'
import { net, powerMonitor } from 'electron'
import type { ConnectionState, WsServerFrame } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { createLogger } from '../../app/logger'
import { getSettings } from '../../app/config-store'
import type { AuthService } from '../auth/auth-service'
import { WsClient, type WsCloseInfo } from './ws-client'

const log = createLogger('conn')

export interface ConnectionEvents {
  state: (state: ConnectionState) => void
  /** 连接建立。`fullSync` 为 true 时需执行 `since=0` 全量同步（FR-CONN-6）。 */
  open: (info: { fullSync: boolean; reconnectAttempt: number }) => void
  /** 一条已识别的服务端帧。 */
  frame: (frame: WsServerFrame) => void
  /** 断开（含被顶号 / 网络故障）。 */
  closed: (info: { authFailure: boolean; code: number }) => void
  /** 进入降级态（FR-CHAT-9）。 */
  degraded: () => void
}

export declare interface ConnectionManager {
  on<K extends keyof ConnectionEvents>(event: K, listener: ConnectionEvents[K]): this
  off<K extends keyof ConnectionEvents>(event: K, listener: ConnectionEvents[K]): this
  emit<K extends keyof ConnectionEvents>(event: K, ...args: Parameters<ConnectionEvents[K]>): boolean
}

export class ConnectionManager extends EventEmitter {
  private client: WsClient
  private auth: AuthService
  private state: ConnectionState = {
    status: 'unauthenticated',
    attempt: 0,
    lastError: null,
    degraded: false
  }
  private reconnectTimer: NodeJS.Timeout | null = null
  /**
   * 本次连接建立时是否需要全量同步。
   *
   * 覆盖 FR-CONN-6 的 `manualReconnectPendingFullSync` 语义（手动重连置 true）、
   * 首次登录（FR-SYNC-4）以及系统唤醒后的补拉（EDGE-L12 ③）。
   * 这里用**单一**标记而非两个字段：三者都归结为「下一次 open 时执行 since=0」。
   */
  private needFullSyncOnNextOpen = true
  private stopped = true
  private powerListenersBound = false

  constructor(opts: { auth: AuthService }) {
    super()
    this.auth = opts.auth
    this.client = new WsClient()

    this.client.on('open', () => this.handleOpen())
    this.client.on('close', (info) => this.handleClose(info))
    this.client.on('error', (err) => {
      this.patchState({ lastError: err.message })
    })
    this.client.on('frame', (frame) => this.handleFrame(frame))
    this.client.on('heartbeatFailed', () => {
      log.warn('心跳失败，关闭连接并重连')
      this.client.close(true)
      this.scheduleReconnect('heartbeat-failed')
    })
  }

  /* --------------------------------- 对外 API ----------------------------- */

  getState(): ConnectionState {
    return { ...this.state }
  }

  isConnected(): boolean {
    return this.state.status === 'connected' && this.client.isOpen()
  }

  isDegraded(): boolean {
    return this.state.degraded
  }

  /** 登录成功后启动（FR-CONN-1）。 */
  start(): void {
    this.stopped = false
    this.needFullSyncOnNextOpen = true
    this.bindPowerListeners()
    this.connectNow('login')
  }

  /** FR-AUTH-7：退出登录时断开。 */
  stop(): void {
    this.stopped = true
    this.clearReconnectTimer()
    this.client.close(true)
    this.patchState({ status: 'unauthenticated', attempt: 0, lastError: null, degraded: false })
  }

  /** FR-CONN-6：手动重连（重置退避、置全量同步标记）。 */
  reconnectManual(): void {
    log.info('手动重连，重置退避计数并标记全量同步')
    this.needFullSyncOnNextOpen = true
    this.clearReconnectTimer()
    this.stopped = false
    this.patchState({ attempt: 0, degraded: false })
    this.connectNow('manual')
  }

  /** FR-CONN-7 / EDGE-L12：系统唤醒或网络恢复时**立即**重连，不等退避。 */
  reconnectNow(reason: string): void {
    if (this.stopped) return
    if (this.isConnected()) {
      // 已连接时唤醒只需触发一次全量同步补拉（EDGE-L12 ③）
      log.info('已在连接状态，改为触发全量同步', { reason })
      this.needFullSyncOnNextOpen = true
      this.emit('open', { fullSync: true, reconnectAttempt: this.state.attempt })
      return
    }
    log.info('立即重连（跳过退避）', { reason })
    this.clearReconnectTimer()
    this.connectNow(reason)
  }

  /** 发送一帧；返回是否成功（FR-CHAT-3）。 */
  send(frame: { type: string; [k: string]: unknown }): boolean {
    return this.client.send(frame as never)
  }

  /* --------------------------------- 内部 --------------------------------- */

  private connectNow(reason: string): void {
    const token = this.auth.getAccessToken()
    if (!token) {
      log.warn('无 Access Token，无法建立 WS 连接')
      this.patchState({ status: 'unauthenticated', lastError: 'no-token' })
      return
    }
    const { wsBaseUrl } = getSettings()
    this.patchState({ status: this.state.attempt > 0 ? 'reconnecting' : 'connecting', lastError: null })
    log.info('发起连接', { reason, attempt: this.state.attempt })
    this.client.connect(wsBaseUrl, token)
  }

  private handleOpen(): void {
    const fullSync = this.needFullSyncOnNextOpen
    const attempt = this.state.attempt
    this.needFullSyncOnNextOpen = false
    this.patchState({ status: 'connected', attempt: 0, lastError: null, degraded: false })
    log.info('连接成功', { fullSync, afterAttempts: attempt })
    this.emit('open', { fullSync, reconnectAttempt: attempt })
  }

  private handleClose(info: WsCloseInfo): void {
    const authFailure = WsClient.isAuthFailure(info)
    this.emit('closed', { authFailure, code: info.code })

    if (this.stopped) return

    if (authFailure) {
      // FR-CONN-4：握手 401/403 或 4001 **不自动重连**，改走 Token 刷新；
      // 重连前会先清空退避计数。
      log.info('鉴权失败关闭，走 Token 刷新流程（不自动重连）')
      this.patchState({ status: 'refreshing', attempt: 0, lastError: null })
      void this.refreshAndReconnect()
      return
    }

    this.scheduleReconnect('close', info.code)
  }

  private async refreshAndReconnect(): Promise<void> {
    const ok = await this.auth.handleWsAuthExpired()
    if (this.stopped) return
    if (ok) {
      // 刷新成功 → 用新 Token 重连，退避计数已清零
      this.patchState({ attempt: 0 })
      this.connectNow('token-refreshed')
      return
    }
    // 刷新失败（或纯网络问题）：若凭据已彻底失效，auth 服务会触发跳登录页
    if (!this.auth.describe().authenticated) {
      this.patchState({ status: 'unauthenticated' })
      return
    }
    // 网络问题：复位退避后按普通流程重试
    log.info('刷新未成功但凭据仍有效，按普通重连流程重试')
    this.patchState({ attempt: 0 })
    this.scheduleReconnect('refresh-network-failure')
  }

  private scheduleReconnect(reason: string, closeCode?: number): void {
    if (this.stopped) return
    this.clearReconnectTimer()

    const attempt = this.state.attempt + 1
    if (attempt > PROTOCOL.RECONNECT_MAX_ATTEMPTS) {
      log.error('重连次数达上限，进入降级态', { attempts: attempt - 1 })
      this.patchState({ status: 'degraded', degraded: true, attempt: attempt - 1, lastError: reason })
      this.emit('degraded')
      return
    }

    // FR-CONN-3：退避 2^n 秒，上限 60 秒
    const delay = Math.min(PROTOCOL.RECONNECT_BASE_MS * 2 ** (attempt - 1), PROTOCOL.RECONNECT_MAX_MS)
    log.info('计划重连', { attempt, delayMs: delay, reason, closeCode })
    this.patchState({ status: 'reconnecting', attempt, lastError: reason })

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null
      this.connectNow('backoff-elapsed')
    }, delay)
    this.reconnectTimer.unref?.()
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
  }

  /**
   * 帧分发。NFR-12：未知 `type` 只记 debug 日志并忽略，绝不抛错。
   */
  private handleFrame(frame: Record<string, unknown> | null): void {
    if (!frame) return
    const type = frame.type
    if (typeof type !== 'string') return

    const known = new Set([
      'pong',
      'chat.message.echo',
      'chat.queued',
      'chat.typing',
      'chat.reply.stream',
      'bot.error',
      'memory.fact.created',
      'auth.expired',
      'points.changed',
      'level.changed',
      'streak.warning',
      'makeup_card.changed',
      'points.snapshot'
    ])
    if (!known.has(type)) {
      log.debug('忽略未知帧类型（NFR-12 向后兼容）', { type })
      return
    }

    if (type === 'auth.expired') {
      log.warn('收到 auth.expired，进入刷新流程')
      this.patchState({ status: 'refreshing' })
      void this.refreshAndReconnect()
      return
    }

    this.emit('frame', frame as unknown as WsServerFrame)
  }

  /** FR-CONN-7 / EDGE-L12：监听休眠唤醒与网络状态。 */
  private bindPowerListeners(): void {
    if (this.powerListenersBound) return
    this.powerListenersBound = true

    try {
      powerMonitor.on('resume', () => {
        log.info('系统唤醒（powerMonitor.resume）')
        this.reconnectNow('power-resume')
      })
      powerMonitor.on('unlock-screen', () => {
        this.reconnectNow('unlock-screen')
      })
    } catch (err) {
      log.warn('powerMonitor 不可用', { error: String(err) })
    }

    // Electron 无网络变化事件，用低频轮询 `net.isOnline()` 做在线状态跃迁检测
    this.netPoll = setInterval(() => {
      if (this.stopped) return
      let online = true
      try {
        online = net.isOnline()
      } catch {
        online = true
      }
      if (online && !this.lastOnline) {
        log.info('网络恢复')
        this.reconnectNow('network-online')
      }
      this.lastOnline = online
    }, 10_000)
    this.netPoll.unref?.()
  }

  private netPoll: NodeJS.Timeout | null = null
  private lastOnline = true

  dispose(): void {
    this.stopped = true
    this.clearReconnectTimer()
    if (this.netPoll) {
      clearInterval(this.netPoll)
      this.netPoll = null
    }
    this.client.close(true)
  }

  private patchState(patch: Partial<ConnectionState>): void {
    this.state = { ...this.state, ...patch }
    this.emit('state', this.getState())
  }
}
