/**
 * REST 客户端（FR-AUTH-4 / FR-AUTH-5 / FR-NET-1 / §5.2）。
 *
 * 关键行为：
 * - 每个请求生成 `trace_{uuid}` 写入 `x-trace-id` 并记入本地日志（FR-NET-1）。
 * - 401 自动调用 `/auth/refresh` 续签并**重放**原请求（FR-AUTH-4，对标 `TokenAuthenticator`）。
 * - 同一时刻只允许一个刷新请求在途，其余请求排队等待（FR-AUTH-5，
 *   避免并发 401 触发多次轮转导致 Refresh Token 互相作废）。
 * - 业务失败有两种约定，必须都处理（§5.2）：
 *     ① 积分路由：HTTP 200 + `code != 0`
 *     ② `http_api` 鉴权失败：`{"detail": {"code": 40101, ...}}`（`code` 不在顶层）
 * - 互动接口超时需 **> 60s**（FR-INT-8），按请求单独放宽。
 */

import { randomUUID } from 'node:crypto'
import { fetch as undiciFetch } from 'undici'
import type { ApiEnvelope } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { extractApiErrorCode, extractApiErrorMessage, TksApiError } from '@shared/errors'
import { createLogger } from '../../app/logger'
import { dispatcherFor, egressInfo } from './dispatcher'

const log = createLogger('rest')

/**
 * 超时/取消的专用错误类型。
 *
 * ⚠️ 不要退回 `controller.abort(new Error('timeout'))` + 靠 `err.name`/消息文本判断：
 * `abort(reason)` 抛出的就是传入的那个对象，`name` 是 `'Error'` 而非 `'AbortError'`，
 * 于是超时会被误判成网络错误，并落进「超时禁止自动重试」的反向分支。
 */
class RequestAbortError extends Error {
  readonly kind: 'timeout' | 'cancelled'
  constructor(kind: 'timeout' | 'cancelled') {
    super(kind === 'timeout' ? 'request timeout' : 'request cancelled')
    this.name = 'RequestAbortError'
    this.kind = kind
  }
}

function classifyAbort(err: unknown): 'timeout' | 'cancelled' | null {
  if (err instanceof RequestAbortError) return err.kind
  // 外部 signal 未携带 reason 时，undici 抛标准 AbortError
  if ((err as Error)?.name === 'AbortError') return 'cancelled'
  return null
}

/** 网络抖动重试的退避参数（仅用于「服务端未收到请求」的连接层失败）。 */
const RETRY_BASE_MS = 800
const RETRY_MAX_MS = 6_000
const RETRY_MAX_ATTEMPTS = 3

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  query?: Record<string, string | number | boolean | null | undefined>
  body?: unknown
  /** 默认 `PROTOCOL.REST_TIMEOUT_MS`；互动接口传 `PROTOCOL.INTERACTION_TIMEOUT_MS`。 */
  timeoutMs?: number
  /** 是否附加 `Authorization` 头，默认 `true`。 */
  authenticated?: boolean
  /** 是否要求统一信封 `{code,message,data}`，默认 `true`；`/auth/*` 传 `false`。 */
  envelope?: boolean
  /** 401 时是否自动续签重放，默认 `true`；刷新请求自身必须传 `false` 防止递归。 */
  allowRefresh?: boolean
  /** 外部取消信号。 */
  signal?: AbortSignal
  /** 幂等重试次数（网络层抖动），默认 0。 */
  retries?: number
}

export interface AuthHeaderProvider {
  getAccessToken(): string | null
  /** 返回新的 accessToken；失败返回 null。必须自带互斥，见 FR-AUTH-5。 */
  refreshAccessToken(): Promise<string | null>
  onUnauthorized(reason: 'refresh-failed' | 'no-token'): void
}

interface RawResult {
  status: number
  ok: boolean
  json: unknown
  text: string
  /** 只用到 `get()`，故不绑定具体 Headers 实现（global 与 undici 的类型不同）。 */
  headers: { get(name: string): string | null }
}

function buildUrl(base: string, path: string, query?: RequestOptions['query']): string {
  const normalizedBase = base.endsWith('/') ? base : `${base}/`
  const normalizedPath = path.replace(/^\/+/, '')
  let url: URL
  try {
    url = new URL(normalizedPath, normalizedBase)
  } catch {
    // base 不合法时给出可诊断的错误
    throw new TksApiError(`invalid base url: ${base}`, { code: null, httpStatus: null })
  }
  if (query) {
    for (const [key, value] of Object.entries(query)) {
      if (value === null || value === undefined) continue
      url.searchParams.set(key, String(value))
    }
  }
  return url.toString()
}

export class RestClient {
  private auth: AuthHeaderProvider
  private baseUrlProvider: () => string

  constructor(opts: { auth: AuthHeaderProvider; baseUrlProvider: () => string }) {
    this.auth = opts.auth
    this.baseUrlProvider = opts.baseUrlProvider
  }

  setAuthProvider(auth: AuthHeaderProvider): void {
    this.auth = auth
  }

  private async raw(
    url: string,
    method: string,
    headers: Record<string, string>,
    body: unknown,
    timeoutMs: number,
    signal?: AbortSignal
  ): Promise<RawResult> {
    const controller = new AbortController()
    // 用专用错误对象标记「这是超时」，避免靠字符串猜测（见 RequestAbortError 注释）
    const timer = setTimeout(() => controller.abort(new RequestAbortError('timeout')), timeoutMs)
    const onOuterAbort = (): void => controller.abort(new RequestAbortError('cancelled'))
    signal?.addEventListener('abort', onOuterAbort, { once: true })

    try {
      const res = await undiciFetch(url, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
        redirect: 'follow',
        // 关键：显式指定出口。全局 fetch 不读代理环境变量（问题 1）
        dispatcher: dispatcherFor(url)
      })
      const text = await res.text()
      let json: unknown = null
      if (text) {
        try {
          json = JSON.parse(text)
        } catch {
          json = null
        }
      }
      return { status: res.status, ok: res.ok, json, text, headers: res.headers }
    } finally {
      clearTimeout(timer)
      signal?.removeEventListener('abort', onOuterAbort)
    }
  }

  /** 发一个请求；带信封解包、业务错误码判定与 401 续签重放。 */
  async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    return this.requestWithAuthRetry<T>(path, options, 0)
  }

  /**
   * @param authRetry 已经因 401 重放过几次。上限 1 次（即总共 2 次尝试）：
   *   首次 401 → 续签 → 重放；若重放仍 401（例如期间发生了另一次轮换把 token 换掉），
   *   再续签重放一次；仍失败则放弃并抛错，避免无限循环。
   *   互斥本身由 `AuthService` 的单一在途 Promise 保证（FR-AUTH-5）。
   */
  private async requestWithAuthRetry<T>(path: string, options: RequestOptions, authRetry: number): Promise<T> {
    const MAX_AUTH_RETRIES = 1
    const {
      method = 'GET',
      query,
      body,
      timeoutMs = PROTOCOL.REST_TIMEOUT_MS,
      authenticated = true,
      envelope = true,
      allowRefresh = true,
      signal,
      retries = 0
    } = options

    const url = buildUrl(this.baseUrlProvider(), path, query)
    const traceId = `trace_${randomUUID()}`
    const started = Date.now()

    const headers: Record<string, string> = {
      'x-trace-id': traceId,
      accept: 'application/json'
    }
    if (body !== undefined) headers['content-type'] = 'application/json'
    if (authenticated) {
      const token = this.auth.getAccessToken()
      if (token) headers.authorization = `Bearer ${token}`
    }

    log.debug('请求', { method, path, traceId, authenticated, authRetry })

    let result: RawResult
    try {
      result = await this.raw(url, method, headers, body, timeoutMs, signal)
    } catch (err) {
      const abortKind = classifyAbort(err)
      const message =
        abortKind === 'timeout'
          ? 'request timeout'
          : abortKind === 'cancelled'
            ? 'request cancelled'
            : `network error: ${String(err)}`
      const elapsed = Date.now() - started
      log.warn('请求失败', {
        method,
        path,
        traceId,
        error: message,
        ms: elapsed,
        egress: egressInfo().kind,
        retriesLeft: retries
      })

      // 用户/上层主动取消：直接抛出，不重试
      if (abortKind === 'cancelled') {
        throw new TksApiError(message, { code: null, httpStatus: null, traceId })
      }
      // 超时不自动重试：互动接口可能已在服务端扣分，重试有重复扣分风险
      // （服务端有幂等键兜底，但保守起见不重试）
      if (abortKind === 'timeout') {
        throw new TksApiError('request timeout', { code: null, httpStatus: null, traceId })
      }
      // 纯网络层抖动（连不上 / TLS 失败）：服务端未收到请求，重试安全
      if (retries > 0) {
        const backoffMs = Math.min(RETRY_BASE_MS * 2 ** (RETRY_MAX_ATTEMPTS - retries), RETRY_MAX_MS)
        log.info('网络抖动，退避重试', { method, path, traceId, backoffMs, retriesLeft: retries })
        await sleep(backoffMs)
        return this.requestWithAuthRetry<T>(path, { ...options, retries: retries - 1 }, authRetry)
      }
      throw new TksApiError(message, { code: null, httpStatus: null, traceId })
    }

    log.debug('响应', { method, path, traceId, status: result.status, ms: Date.now() - started })

    // ── 401：Token 失效 → 续签并重放（FR-AUTH-4）
    if (result.status === 401 && authenticated && allowRefresh && authRetry < MAX_AUTH_RETRIES) {
      const code = extractApiErrorCode(result.json)
      log.info('收到 401，尝试续签', { path, traceId, code, authRetry })
      const newToken = await this.auth.refreshAccessToken()
      if (!newToken) {
        this.auth.onUnauthorized('refresh-failed')
        throw new TksApiError(extractApiErrorMessage(result.json) ?? 'unauthorized', {
          code: code ?? 40101,
          httpStatus: 401,
          traceId
        })
      }
      // 重放（replay 也走完整流程，因此仍有 +1 次续签预算兜底）
      return this.requestWithAuthRetry<T>(path, options, authRetry + 1)
    }

    if (result.status === 401 && authenticated) {
      this.auth.onUnauthorized('refresh-failed')
    }

    return this.unwrap<T>(result, path, traceId, envelope)
  }

  private unwrap<T>(result: RawResult, path: string, traceId: string, envelope: boolean): T {
    // ── 非 2xx：错误码可能在顶层，也可能在 `detail.code`（§5.2 形状陷阱）
    if (!result.ok) {
      const code = extractApiErrorCode(result.json)
      const message = extractApiErrorMessage(result.json) ?? `HTTP ${result.status}`
      log.warn('请求返回错误状态', { path, traceId, status: result.status, code })
      throw new TksApiError(message, { code, httpStatus: result.status, traceId })
    }

    // ── 2xx：判断业务失败（HTTP 200 + code != 0，积分路由的约定）
    if (envelope) {
      const body = result.json as ApiEnvelope<T> | null
      if (!body || typeof body !== 'object' || !('code' in body)) {
        // 少数接口（如 ws_api 的 /healthz）无信封形态；NFR-12：宽容处理
        return result.json as T
      }
      if (body.code !== 0) {
        log.warn('业务错误', { path, traceId, code: body.code, message: body.message })
        throw new TksApiError(body.message ?? `business error ${body.code}`, {
          code: body.code,
          httpStatus: result.status,
          traceId: body.traceId ?? traceId
        })
      }
      return body.data
    }

    return result.json as T
  }

  /**
   * 连通性自检（FR-SET-3）。
   *
   * ⚠️ §5.2：`http_api` 与 `ws_api` **各有一个 `/healthz` 且形态不同**
   * （前者带信封，后者 `{status:"ok",service:"ws_api"}` 无信封）。
   * 因此这里**只判断 HTTP 200，不解析响应体结构**。
   */
  async healthz(apiBaseUrl?: string, timeoutMs = 8_000): Promise<{ ok: boolean; latencyMs: number | null; status: number | null }> {
    const url = buildUrl(apiBaseUrl ?? this.baseUrlProvider(), 'healthz')
    const started = Date.now()
    try {
      const res = await this.raw(url, 'GET', { accept: 'application/json' }, undefined, timeoutMs)
      return { ok: res.status === 200, latencyMs: Date.now() - started, status: res.status }
    } catch {
      return { ok: false, latencyMs: null, status: null }
    }
  }
}
