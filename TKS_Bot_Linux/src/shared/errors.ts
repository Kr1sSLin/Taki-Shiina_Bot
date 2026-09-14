/**
 * 服务端 / 客户端错误码与本地化键的映射（§5.3.3、§7.0）。
 *
 * NFR-10：界面文案统一走 i18n 资源文件，**代码中不硬编码中文**。
 * 这里只维护「错误码 → i18n key」的映射，具体文案在 `renderer/src/i18n/locales/zh-CN.ts`。
 */

import type { ApiErrorDetailBody, AppErrorCode } from './protocol'

/** 服务端 WS 错误码 → i18n key（§5.3.3）。 */
export const SERVER_ERROR_I18N: Record<string, string> = {
  INVALID_JSON: 'error.server.INVALID_JSON',
  UNKNOWN_TYPE: 'error.server.UNKNOWN_TYPE',
  EMPTY_MESSAGE: 'error.server.EMPTY_MESSAGE',
  VISION_IMAGE_COUNT_EXCEEDED: 'error.server.VISION_IMAGE_COUNT_EXCEEDED',
  VISION_INVALID_MIME: 'error.server.VISION_INVALID_MIME',
  VISION_INVALID_BASE64: 'error.server.VISION_INVALID_BASE64',
  VISION_IMAGE_TOO_LARGE: 'error.server.VISION_IMAGE_TOO_LARGE',
  GEMINI_NOT_CONFIGURED: 'error.server.GEMINI_NOT_CONFIGURED',
  AI_TIMEOUT: 'error.server.AI_TIMEOUT',
  INTERNAL_ERROR: 'error.server.INTERNAL_ERROR'
}

/** 客户端自有错误码 → i18n key（§5.3.3）。 */
export const CLIENT_ERROR_I18N: Record<string, string> = {
  SEND_FAILED: 'error.client.SEND_FAILED',
  TIMEOUT: 'error.client.TIMEOUT',
  CONNECTION_LOST: 'error.client.CONNECTION_LOST'
}

/** HTTP / 业务错误码 → i18n key（§5.2、§7.0）。 */
export const HTTP_ERROR_I18N: Record<number, string> = {
  40001: 'error.api.40001',
  40002: 'error.api.40002',
  40101: 'error.api.40101',
  40102: 'error.api.40102',
  40301: 'error.api.40301',
  // ⚠️ EDGE-L3：40302 是后端死代码（DeviceLimitError 从未被抛出），仅作兜底文案。
  40302: 'error.api.40302',
  40201: 'error.api.40201',
  40202: 'error.api.40202',
  40204: 'error.api.40204',
  40205: 'error.api.40205',
  40206: 'error.api.40206',
  40207: 'error.api.40207',
  5000: 'error.api.5000'
}

export function errorI18nKey(code: string | number | null | undefined): string {
  if (code === null || code === undefined || code === '') return 'error.unknown'
  if (typeof code === 'number') return HTTP_ERROR_I18N[code] ?? 'error.unknown'
  return SERVER_ERROR_I18N[code] ?? CLIENT_ERROR_I18N[code] ?? 'error.unknown'
}

/**
 * 从响应体中提取业务错误码，兼容两种形状（§5.2）：
 *   ① 顶层 `code`（积分路由 / 业务失败）
 *   ② `detail.code`（`http_api` 鉴权失败，FastAPI 二次包装）
 */
export function extractApiErrorCode(body: unknown): number | null {
  if (!body || typeof body !== 'object') return null
  const b = body as ApiErrorDetailBody
  if (typeof b.code === 'number') return b.code
  if (b.detail && typeof b.detail.code === 'number') return b.detail.code
  return null
}

/** 从响应体中提取业务错误消息（同样兼容两种形状）。 */
export function extractApiErrorMessage(body: unknown): string | null {
  if (!body || typeof body !== 'object') return null
  const b = body as ApiErrorDetailBody
  if (typeof b.message === 'string' && b.message) return b.message
  if (b.detail && typeof b.detail.message === 'string' && b.detail.message) return b.detail.message
  if (b.detail && typeof b.detail === 'string') return b.detail
  return null
}

/** 结构化错误：跨 IPC 传输时保留 code，渲染端用 code 查 i18n。 */
export class TksApiError extends Error {
  readonly code: number | null
  readonly httpStatus: number | null
  readonly traceId: string | null

  constructor(message: string, opts: { code?: number | null; httpStatus?: number | null; traceId?: string | null } = {}) {
    super(message)
    this.name = 'TksApiError'
    this.code = opts.code ?? null
    this.httpStatus = opts.httpStatus ?? null
    this.traceId = opts.traceId ?? null
  }
}

/** IPC 统一失败信封（Electron 会丢失自定义 Error 字段，故显式序列化）。 */
export interface IpcFailure {
  __tksError: true
  message: string
  code: number | null
  appErrorCode: AppErrorCode | null
  i18nKey: string
}

export function toIpcFailure(err: unknown): IpcFailure {
  if (err instanceof TksApiError) {
    return {
      __tksError: true,
      message: err.message,
      code: err.code,
      appErrorCode: null,
      i18nKey: errorI18nKey(err.code)
    }
  }
  const message = err instanceof Error ? err.message : String(err)
  return { __tksError: true, message, code: null, appErrorCode: null, i18nKey: 'error.unknown' }
}

export function isIpcFailure(value: unknown): value is IpcFailure {
  return !!value && typeof value === 'object' && (value as IpcFailure).__tksError === true
}

/* -------------------------------------------------------------------------- */
/* 跨 contextBridge 的错误编码                                                  */
/* -------------------------------------------------------------------------- */

/**
 * ⚠️ **关键约束（曾导致真实缺陷）**：`contextBridge` 在隔离世界之间传递 `Error` 时，
 * 只保留 `message` / `stack` / `name`，**自定义属性会被丢弃**。
 *
 * 也就是说 preload 里 `err.code = 40101` 到了渲染进程就变成 `undefined`，于是：
 *   - `errorCodeOf(err)` 恒为 null → 40201 / 40204 / 40206 等业务分支全部失效
 *   - `err.i18nKey` 丢失 → 无法按错误码查文案（FR-AUTH-8 等）
 *
 * 解决办法：把载荷**编码进 message**（唯一保证能穿过桥的字段），
 * 渲染端用 `decodeIpcErrorPayload` 还原。
 * 自定义属性仍然保留 —— 同上下文调用与单元测试可直接读，且万一将来 Electron
 * 开始保留属性也不会失效。
 */
export const IPC_ERROR_MARK = 'TKS_IPC_ERR '

export interface IpcErrorPayload {
  code: number | null
  i18nKey: string
  appErrorCode: string | null
  /** 服务端 / 主进程给出的原始可读消息（可能为空）。 */
  message: string
}

export function encodeIpcErrorPayload(payload: IpcErrorPayload): string {
  return `${IPC_ERROR_MARK}${JSON.stringify(payload)}`
}

export function decodeIpcErrorPayload(message: string | null | undefined): IpcErrorPayload | null {
  if (typeof message !== 'string' || !message.startsWith(IPC_ERROR_MARK)) return null
  try {
    const parsed = JSON.parse(message.slice(IPC_ERROR_MARK.length)) as Partial<IpcErrorPayload> | null
    if (!parsed || typeof parsed !== 'object') return null
    return {
      code: typeof parsed.code === 'number' ? parsed.code : null,
      i18nKey: typeof parsed.i18nKey === 'string' && parsed.i18nKey ? parsed.i18nKey : 'error.unknown',
      appErrorCode: typeof parsed.appErrorCode === 'string' ? parsed.appErrorCode : null,
      message: typeof parsed.message === 'string' ? parsed.message : ''
    }
  } catch {
    return null
  }
}

/**
 * 从任意异常提取 IPC 错误载荷，兼容两种来源：
 *  ① message 里的编码（跨 contextBridge 后唯一可靠的通道）
 *  ② 自定义属性（同上下文调用时直接可读）
 */
export function readIpcErrorPayload(err: unknown): IpcErrorPayload | null {
  if (!err || typeof err !== 'object') return null

  const fromMessage = decodeIpcErrorPayload((err as { message?: string }).message)
  if (fromMessage) return fromMessage

  const anyErr = err as { code?: unknown; i18nKey?: unknown; appErrorCode?: unknown; message?: unknown }
  const hasProps = typeof anyErr.code === 'number' || typeof anyErr.i18nKey === 'string'
  if (!hasProps) return null
  return {
    code: typeof anyErr.code === 'number' ? anyErr.code : null,
    i18nKey: typeof anyErr.i18nKey === 'string' && anyErr.i18nKey ? anyErr.i18nKey : 'error.unknown',
    appErrorCode: typeof anyErr.appErrorCode === 'string' ? anyErr.appErrorCode : null,
    message: typeof anyErr.message === 'string' ? anyErr.message : ''
  }
}
