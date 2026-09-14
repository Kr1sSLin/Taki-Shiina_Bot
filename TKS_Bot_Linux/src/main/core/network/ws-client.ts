/**
 * WebSocket 传输层（§5.3，FR-CONN-1/2/3/4）。
 *
 * 为什么必须在主进程用 `ws` 库：后端 WS 鉴权的**首选方式是请求头**
 * `Authorization: Bearer {token}`（`ws_api.py::_extract_ws_auth` 三级回退的第 1 级），
 * 而浏览器 `WebSocket` 不支持自定义请求头。URL query `?token=` 已被后端明确拒绝。
 *
 * NFR-12：解析服务端消息时必须忽略未知字段与未知 `type`，**不得抛错**。
 */

import { EventEmitter } from 'node:events'
import WebSocket from 'ws'
import { PROTOCOL, WS_CLOSE_INVALID_TOKEN } from '@shared/levels'
import type { WsClientFrame } from '@shared/protocol'
import { createLogger } from '../../app/logger'

const log = createLogger('ws')

export type WsStatus = 'idle' | 'connecting' | 'open' | 'closing' | 'closed'

export interface WsCloseInfo {
  code: number
  reason: string
  /** 握手阶段失败时的 HTTP 状态码（如 401/403）。 */
  handshakeStatus: number | null
  wasClean: boolean
}

export interface WsClientEvents {
  open: () => void
  close: (info: WsCloseInfo) => void
  error: (err: Error) => void
  /** 原始下行帧（已 JSON 解析；无法解析时为 null）。 */
  frame: (frame: Record<string, unknown> | null, raw: string) => void
  /** 心跳发送失败（FR-CONN-2：立即触发重连）。 */
  heartbeatFailed: (err: Error) => void
}

export declare interface WsClient {
  on<K extends keyof WsClientEvents>(event: K, listener: WsClientEvents[K]): this
  off<K extends keyof WsClientEvents>(event: K, listener: WsClientEvents[K]): this
  emit<K extends keyof WsClientEvents>(event: K, ...args: Parameters<WsClientEvents[K]>): boolean
}

export class WsClient extends EventEmitter {
  private socket: WebSocket | null = null
  private heartbeatTimer: NodeJS.Timeout | null = null
  private status: WsStatus = 'idle'
  private url = ''
  private missedPongs = 0

  getStatus(): WsStatus {
    return this.status
  }

  isOpen(): boolean {
    return this.status === 'open' && this.socket?.readyState === WebSocket.OPEN
  }

  /**
   * 建立连接。`/ws/chat` 为固定路径（§5.3）。
   * @param wsBaseUrl 形如 `wss://takishiinabot.top`（不含路径）
   */
  connect(wsBaseUrl: string, token: string): void {
    this.close(true)
    this.url = `${wsBaseUrl.replace(/\/+$/, '')}/ws/chat`
    this.status = 'connecting'
    this.missedPongs = 0

    log.info('WS 连接中', { url: this.url })

    const socket = new WebSocket(this.url, {
      headers: {
        // 三级回退的第 1 级：请求头鉴权（Linux 端采用）
        Authorization: `Bearer ${token}`
      },
      handshakeTimeout: 15_000,
      // 关闭 permessage-deflate 以获得更低的首帧延迟（NFR-4）
      perMessageDeflate: false
    })
    this.socket = socket

    socket.on('open', () => {
      this.status = 'open'
      log.info('WS 已连接')
      this.startHeartbeat()
      this.emit('open')
    })

    socket.on('message', (data: WebSocket.RawData, isBinary: boolean) => {
      // NFR-12：解析失败不抛错，只记日志并跳过
      let raw: string
      try {
        raw = isBinary ? Buffer.from(data as Buffer).toString('utf8') : String(data)
      } catch {
        return
      }
      let parsed: Record<string, unknown> | null = null
      try {
        const value = JSON.parse(raw)
        parsed = value && typeof value === 'object' ? (value as Record<string, unknown>) : null
      } catch {
        log.warn('收到非 JSON 帧，已忽略', { length: raw.length })
        parsed = null
      }
      if (parsed) this.handleFrame(parsed)
      this.emit('frame', parsed, raw)
    })

    socket.on('pong', () => {
      this.missedPongs = 0
    })

    socket.on('error', (err: Error) => {
      log.warn('WS 错误', { error: err.message })
      this.emit('error', err)
    })

    socket.on('unexpected-response', (_req, res) => {
      // 握手返回非 101：401/403 需走 Token 刷新而非普通重连（FR-CONN-4）
      const status = res.statusCode ?? null
      log.warn('WS 握手失败', { status })
      this.stopHeartbeat()
      this.status = 'closed'
      this.socket = null
      this.emit('close', {
        code: 1006,
        reason: `handshake ${status ?? 'unknown'}`,
        handshakeStatus: status,
        wasClean: false
      })
    })

    socket.on('close', (code: number, reasonBuf: Buffer) => {
      const reason = reasonBuf?.toString?.('utf8') ?? ''
      this.stopHeartbeat()
      this.status = 'closed'
      this.socket = null
      log.info('WS 已断开', { code, reason })
      this.emit('close', { code, reason, handshakeStatus: null, wasClean: code === 1000 })
    })
  }

  private handleFrame(frame: Record<string, unknown>): void {
    const type = frame.type
    if (typeof type !== 'string') return

    // `pong` 是顶层 `{type,timestamp}`，没有 payload 包装（§5.3.2）
    if (type === 'pong') {
      this.missedPongs = 0
      return
    }

    // 服务端收到 ping 后向该账号**所有**连接广播 pong，因此无需自行判定存活，
    // 只在连续多个心跳周期完全无 pong 时告警（服务端无超时踢人策略）。
    if (type === 'auth.expired') {
      log.warn('服务端下发 auth.expired', { payload: frame.payload })
    }
  }

  /* ------------------------------ 心跳（FR-CONN-2） ----------------------- */

  private startHeartbeat(): void {
    this.stopHeartbeat()
    this.heartbeatTimer = setInterval(() => {
      if (!this.isOpen()) return
      if (this.missedPongs >= 3) {
        const err = new Error('heartbeat timeout: no pong for 3 cycles')
        log.warn('心跳无响应，触发重连')
        this.emit('heartbeatFailed', err)
        return
      }
      this.missedPongs += 1
      const ok = this.send({
        type: 'ping',
        payload: { timestamp: Date.now() }
      })
      if (!ok) {
        this.emit('heartbeatFailed', new Error('heartbeat send failed'))
      }
    }, PROTOCOL.HEARTBEAT_INTERVAL_MS)
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer)
      this.heartbeatTimer = null
    }
  }

  /* --------------------------------- 发送 -------------------------------- */

  /** @returns 是否成功写入 socket（FR-CHAT-3：返回 false 时置 `SEND_FAILED`）。 */
  send(frame: WsClientFrame): boolean {
    if (!this.isOpen() || !this.socket) {
      log.debug('发送失败：连接未就绪', { type: frame.type })
      return false
    }
    try {
      this.socket.send(JSON.stringify(frame))
      return true
    } catch (err) {
      log.warn('发送异常', { type: frame.type, error: String(err) })
      return false
    }
  }

  /** 关闭连接。`silent=true` 时不触发 close 事件外的重连逻辑（用于主动切换地址）。 */
  close(silent = false): void {
    this.stopHeartbeat()
    const socket = this.socket
    this.socket = null
    this.status = 'closing'
    if (!socket) {
      this.status = 'closed'
      return
    }
    try {
      socket.removeAllListeners('close')
      socket.removeAllListeners('error')
      socket.removeAllListeners('unexpected-response')
      if (!silent) {
        socket.on('close', () => {
          this.status = 'closed'
        })
      }
      socket.close(1000, 'client closing')
      // 兜底强制销毁，避免挂起
      setTimeout(() => {
        try {
          socket.terminate()
        } catch {
          /* ignore */
        }
      }, 1500).unref?.()
    } catch (err) {
      log.warn('关闭 WS 异常', { error: String(err) })
    }
    this.status = 'closed'
  }

  /** 判定该关闭是否属于「鉴权失败，需刷新 Token 而非重连」。 */
  static isAuthFailure(info: WsCloseInfo): boolean {
    if (info.handshakeStatus === 401 || info.handshakeStatus === 403) return true
    if (info.code === WS_CLOSE_INVALID_TOKEN) return true
    // 后端先 accept 再下发 auth.expired，随后以 4001 关闭
    return /invalid token/i.test(info.reason)
  }
}
