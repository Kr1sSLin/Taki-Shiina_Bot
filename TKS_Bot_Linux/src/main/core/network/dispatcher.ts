/**
 * REST 出口 dispatcher（问题 1 修复）。
 *
 * 背景：Node 的全局 `fetch` 由 undici 实现，**不读 `HTTP_PROXY`/`HTTPS_PROXY`**。
 * 而 WebSocket（`ws` + 长连接）只握手一次并长期复用，REST 每次同步都新建 TLS 连接。
 * 于是在代理可用但直连劣化的网络下会出现「消息发得出去（WS 复用旧连接），
 * 历史同步却反复失败（每次新连接都可能建不起来）」——正是本次故障的主因。
 *
 * 这里显式构造 dispatcher：
 * - 命中代理环境变量（且不在 `NO_PROXY` 名单）→ `ProxyAgent`
 * - 否则 → 普通 `Agent`，但显式给出连接超时与 keep-alive，让 REST 也能复用连接
 *
 * 用户可用 `TKS_PROXY` 显式指定，或 `TKS_PROXY=direct` 强制直连（排障用）。
 */

import { Agent, ProxyAgent, type Dispatcher } from 'undici'
import { createLogger } from '../../app/logger'

const log = createLogger('net')

/** TLS/TCP 建连超时。远小于 REST 总超时，让「连不上」快速失败而不是干等 30s。 */
const CONNECT_TIMEOUT_MS = 8_000
/** 空闲连接保活时长，使连续的同步请求复用同一条 TLS 连接。 */
const KEEP_ALIVE_TIMEOUT_MS = 30_000
const KEEP_ALIVE_MAX_TIMEOUT_MS = 120_000

export type EgressKind = 'proxy' | 'direct'

export interface EgressInfo {
  kind: EgressKind
  /** 代理地址（已脱去凭据），直连时为 null。 */
  proxyUrl: string | null
  /** 来源环境变量名，便于排障。 */
  source: string | null
}

let cachedDispatcher: Dispatcher | null = null
let cachedEgress: EgressInfo | null = null
let cachedNoProxy: string[] | null = null

function env(name: string): string | undefined {
  return process.env[name] ?? process.env[name.toLowerCase()] ?? process.env[name.toUpperCase()]
}

/** 隐去 `http://user:pass@host` 里的凭据，日志安全。 */
function redact(raw: string): string {
  try {
    const u = new URL(raw)
    if (u.username || u.password) {
      u.username = u.username ? '***' : ''
      u.password = u.password ? '***' : ''
    }
    return u.toString()
  } catch {
    return raw
  }
}

function normalizeProxyUrl(raw: string): string | null {
  const trimmed = raw.trim()
  if (!trimmed) return null
  // 允许用户只写 `127.0.0.1:7897`
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`
  try {
    const u = new URL(withScheme)
    if (!u.hostname) return null
    // undici 的 ProxyAgent 只支持 http/https 隧道，socks 需要额外依赖
    if (u.protocol !== 'http:' && u.protocol !== 'https:') {
      log.warn('不支持的代理协议，已忽略', { protocol: u.protocol })
      return null
    }
    return u.toString()
  } catch {
    log.warn('代理地址无法解析，已忽略', { value: redact(trimmed) })
    return null
  }
}

/** 解析代理来源。`TKS_PROXY` 优先，`direct`/`off`/`none` 表示强制直连。 */
function resolveProxy(): { url: string | null; source: string | null } {
  const override = env('TKS_PROXY')
  if (override !== undefined) {
    const v = override.trim().toLowerCase()
    if (v === '' || v === 'direct' || v === 'off' || v === 'none') {
      return { url: null, source: 'TKS_PROXY=direct' }
    }
    return { url: normalizeProxyUrl(override), source: 'TKS_PROXY' }
  }
  for (const name of ['HTTPS_PROXY', 'HTTP_PROXY', 'ALL_PROXY']) {
    const value = env(name)
    if (value && value.trim()) {
      const url = normalizeProxyUrl(value)
      if (url) return { url, source: name }
    }
  }
  return { url: null, source: null }
}

function noProxyList(): string[] {
  if (cachedNoProxy) return cachedNoProxy
  cachedNoProxy = (env('NO_PROXY') ?? '')
    .split(',')
    .map((s) => s.trim().toLowerCase())
    .filter(Boolean)
  return cachedNoProxy
}

/** `NO_PROXY` 匹配：支持 `*`、精确域名、`.suffix` 与裸后缀。 */
export function isNoProxyHost(hostname: string): boolean {
  const host = hostname.toLowerCase()
  for (const entry of noProxyList()) {
    if (entry === '*') return true
    const bare = entry.startsWith('.') ? entry.slice(1) : entry
    if (host === bare || host.endsWith(`.${bare}`)) return true
  }
  return false
}

/** 当前出口信息（供日志与设置页展示）。 */
export function egressInfo(): EgressInfo {
  if (cachedEgress) return cachedEgress
  const { url, source } = resolveProxy()
  cachedEgress = url
    ? { kind: 'proxy', proxyUrl: redact(url), source }
    : { kind: 'direct', proxyUrl: null, source }
  return cachedEgress
}

function buildDirectAgent(): Agent {
  return new Agent({
    connect: { timeout: CONNECT_TIMEOUT_MS },
    keepAliveTimeout: KEEP_ALIVE_TIMEOUT_MS,
    keepAliveMaxTimeout: KEEP_ALIVE_MAX_TIMEOUT_MS
  })
}

/**
 * 取得全局 dispatcher。惰性构造并缓存，因此一个进程内所有 REST 请求共享
 * 同一个连接池——这本身也顺带解决了「每次同步都新建 TLS」的问题。
 */
export function getDispatcher(): Dispatcher {
  if (cachedDispatcher) return cachedDispatcher
  const { url, source } = resolveProxy()
  if (url) {
    try {
      cachedDispatcher = new ProxyAgent({
        uri: url,
        connect: { timeout: CONNECT_TIMEOUT_MS },
        keepAliveTimeout: KEEP_ALIVE_TIMEOUT_MS,
        keepAliveMaxTimeout: KEEP_ALIVE_MAX_TIMEOUT_MS
      })
      log.info('REST 出口：代理', { proxy: redact(url), source, connectTimeoutMs: CONNECT_TIMEOUT_MS })
      return cachedDispatcher
    } catch (err) {
      // 代理构造失败不能让整个客户端不可用，降级直连并留下明确日志
      log.error('代理 dispatcher 构造失败，降级为直连', { proxy: redact(url), source, error: String(err) })
    }
  }
  cachedDispatcher = buildDirectAgent()
  log.info('REST 出口：直连', { source, connectTimeoutMs: CONNECT_TIMEOUT_MS })
  return cachedDispatcher
}

/**
 * 针对单个 URL 选择 dispatcher：`NO_PROXY` 命中的主机走独立的直连 Agent，
 * 其余走全局 dispatcher。
 */
let noProxyAgent: Agent | null = null
export function dispatcherFor(url: string): Dispatcher {
  if (egressInfo().kind === 'proxy') {
    try {
      if (isNoProxyHost(new URL(url).hostname)) {
        noProxyAgent ??= buildDirectAgent()
        return noProxyAgent
      }
    } catch {
      /* URL 不合法时交给上层报错 */
    }
  }
  return getDispatcher()
}

/** 测试/重载用：清空缓存，下次调用重新读取环境变量。 */
export function resetDispatcher(): void {
  cachedDispatcher = null
  cachedEgress = null
  cachedNoProxy = null
  noProxyAgent = null
}
