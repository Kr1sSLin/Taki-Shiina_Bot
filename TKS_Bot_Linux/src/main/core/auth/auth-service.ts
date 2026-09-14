/**
 * 认证服务（FR-AUTH-1..9、EDGE-L2、EDGE-L3）。
 *
 * - 登录 / 续签 / 退出登录
 * - **刷新互斥**（FR-AUTH-5）：同一时刻仅一个刷新在途，其余调用等待同一 Promise，
 *   避免并发 401 触发多次轮换导致 Refresh Token 互相作废。
 * - **区分「被静默顶号」与网络故障**（EDGE-L3）：只有拿到 401 响应才算凭据失效；
 *   网络故障时**保留**本地 Token 并稍后重试。
 */

import type { AuthTokens } from '@shared/protocol'
import { TksApiError, extractApiErrorCode } from '@shared/errors'
import { createLogger } from '../../app/logger'
import { getSettings, updateSettings } from '../../app/config-store'
import { RestClient, type AuthHeaderProvider } from '../network/rest-client'
import {
  clearTokens,
  credentialStorageMode,
  getAccessToken,
  getOrCreateDeviceId,
  getRefreshToken,
  getStoredTokens,
  getUserId,
  isAuthenticated,
  storeTokens,
  type CredentialStorageMode
} from './token-manager'

const log = createLogger('auth')

/** Refresh Token TTL 默认 30 天（§5.4）。用于区分「超期」与「被顶号」。 */
const REFRESH_TTL_MS = 30 * 24 * 3600 * 1000

export type AuthExpiredReason = 'kicked' | 'expired' | 'refresh-failed'

export interface AuthEvents {
  onAuthenticated(tokens: AuthTokens): void
  /** 凭据彻底失效，需要跳登录页（EDGE-L2 / EDGE-L3）。 */
  onAuthExpired(reason: AuthExpiredReason): void
  onLoggedOut(): void
}

export class AuthService implements AuthHeaderProvider {
  private rest: RestClient
  private events: AuthEvents
  /** FR-AUTH-5：在途刷新 Promise，所有并发 401 共享它。 */
  private inFlightRefresh: Promise<string | null> | null = null
  /** 本次会话建立时间，用于 EDGE-L3 判断「被顶号」。 */
  private sessionStartedAt: number | null = null
  /** 已通知过失效，避免重复提示。 */
  private expiredNotified = false

  constructor(opts: { rest: RestClient; events: AuthEvents }) {
    this.rest = opts.rest
    this.events = opts.events
    if (isAuthenticated()) {
      this.sessionStartedAt = getStoredTokens()?.accessTokenExpiresAt ?? Date.now()
    }
  }

  /* ------------------------------- AuthHeaderProvider --------------------- */

  getAccessToken(): string | null {
    return getAccessToken()
  }

  onUnauthorized(reason: 'refresh-failed' | 'no-token'): void {
    if (reason === 'no-token') {
      log.warn('请求需要鉴权但本地无 Token')
      return
    }
    // 刷新失败已在 refreshAccessToken 内部处理通知，此处不重复
  }

  /* ---------------------------------- 登录 -------------------------------- */

  async login(username: string, password: string): Promise<AuthTokens> {
    const deviceId = getOrCreateDeviceId()
    const tokens = await this.rest.request<AuthTokens>('auth/login', {
      method: 'POST',
      body: { username, password, deviceId },
      envelope: false,
      authenticated: false,
      allowRefresh: false
    })

    storeTokens(tokens, deviceId)
    this.sessionStartedAt = Date.now()
    this.expiredNotified = false

    // FR-AUTH-9：只记用户名，不记密码
    updateSettings({ lastUsername: username })

    log.info('登录成功', { userId: tokens.userId, storage: credentialStorageMode() })
    this.events.onAuthenticated(tokens)
    return tokens
  }

  /* ---------------------------------- 刷新 -------------------------------- */

  /**
   * FR-AUTH-5：互斥 + 排队。并发的多个 401 只会触发**一次**真实轮换，
   * 其余调用复用同一结果。
   */
  refreshAccessToken(): Promise<string | null> {
    if (this.inFlightRefresh) {
      log.debug('刷新已在途，复用同一 Promise')
      return this.inFlightRefresh
    }
    this.inFlightRefresh = this.doRefresh().finally(() => {
      this.inFlightRefresh = null
    })
    return this.inFlightRefresh
  }

  private async doRefresh(): Promise<string | null> {
    const refreshToken = getRefreshToken()
    if (!refreshToken) {
      log.warn('无 Refresh Token，无法续签')
      this.notifyExpired('expired')
      return null
    }

    const deviceId = getOrCreateDeviceId()
    try {
      const tokens = await this.rest.request<AuthTokens>('auth/refresh', {
        method: 'POST',
        body: { refreshToken },
        envelope: false,
        authenticated: false,
        // 刷新请求自身不能再触发续签，否则递归
        allowRefresh: false
      })
      storeTokens(tokens, deviceId)
      log.info('Token 续签成功')
      return tokens.accessToken
    } catch (err) {
      const code = err instanceof TksApiError ? err.code : null
      const httpStatus = err instanceof TksApiError ? err.httpStatus : null

      // 网络故障：**保留**本地 Token，稍后重试。绝不能当成凭据失效（EDGE-L3）。
      if (httpStatus === null) {
        log.warn('续签遇到网络故障，保留本地凭据待重试', { error: (err as Error).message })
        return null
      }

      // 拿到 401 响应 → 凭据确实失效
      if (httpStatus === 401 || code === 40102 || code === 40101) {
        this.notifyExpired(this.classifyExpiry())
        return null
      }

      log.error('续签失败（非鉴权错误）', { code, httpStatus })
      return null
    }
  }

  /**
   * EDGE-L3：区分「被静默顶号」与「Refresh Token 超期」。
   *
   * 后端 `RefreshTokenStore.issue()` 在设备数超限时**直接删除最旧设备的 refresh token**
   * （`40302` 是死代码，从不抛出）。因此本端收到 40102 时，
   * 若本次会话建立尚在 Refresh TTL（30 天）内，则极可能被其他设备顶掉。
   */
  private classifyExpiry(): AuthExpiredReason {
    if (this.sessionStartedAt && Date.now() - this.sessionStartedAt < REFRESH_TTL_MS) {
      return 'kicked'
    }
    return 'expired'
  }

  private notifyExpired(reason: AuthExpiredReason): void {
    if (this.expiredNotified) return
    this.expiredNotified = true
    if (reason === 'kicked') {
      log.warn('检测到被其他设备顶号（凭据已被服务端移除）')
    } else {
      log.info('登录已过期，需要重新登录', { reason })
    }
    this.events.onAuthExpired(reason)
  }

  /** WS 收到 `auth.expired` 或握手 401/403 时调用（FR-AUTH-6）。 */
  async handleWsAuthExpired(): Promise<boolean> {
    const token = await this.refreshAccessToken()
    if (token) return true
    // 网络故障导致刷新未成功时，不清凭据、不跳登录页，交由重连退避继续尝试
    if (this.expiredNotified) return false
    log.warn('WS 鉴权失效但刷新未成功（可能是网络问题），保留凭据并稍后重试')
    return false
  }

  /* --------------------------------- 退出 --------------------------------- */

  /**
   * FR-AUTH-7：清除 Token、断开 WS、返回登录页。
   * 本地聊天记录默认保留（与 Android 语义一致）。
   */
  logout(): void {
    clearTokens()
    this.sessionStartedAt = null
    this.expiredNotified = false
    log.info('已退出登录')
    this.events.onLoggedOut()
  }

  /* --------------------------------- 状态 --------------------------------- */

  status(): { authenticated: boolean; userId: string | null; deviceId: string } {
    return {
      authenticated: isAuthenticated(),
      userId: getUserId(),
      deviceId: getOrCreateDeviceId()
    }
  }

  storageMode(): CredentialStorageMode {
    return credentialStorageMode()
  }

  /** 供 UI 显示/调试：绝不暴露 Token 内容。 */
  describe(): { authenticated: boolean; userId: string | null; deviceId: string; storage: CredentialStorageMode } {
    const deviceId = getOrCreateDeviceId()
    return { authenticated: isAuthenticated(), userId: getUserId(), deviceId, storage: credentialStorageMode() }
  }

  /** 供设置页展示：当前 deviceId 是否已在服务端白名单（无法本地判断，仅返回 ID）。 */
  currentDeviceId(): string {
    return getOrCreateDeviceId()
  }

  /** 上次登录用户名（FR-AUTH-9）。 */
  lastUsername(): string {
    return getSettings().lastUsername
  }

  /** 供测试与排障：解析出的错误码。 */
  static codeOf(err: unknown): number | null {
    if (err instanceof TksApiError) return err.code
    return extractApiErrorCode(err)
  }
}
