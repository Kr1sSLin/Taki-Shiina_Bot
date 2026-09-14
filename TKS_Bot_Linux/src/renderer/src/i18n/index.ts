/**
 * 渲染进程 i18n（NFR-10）。
 *
 * 语言包与主进程共用 `@shared/i18n`；本文件提供 React 集成与
 * 统一的错误 → 文案转换（关键：错误对象只带 code / i18nKey，文案在这里落地）。
 */

import { useCallback, useMemo } from 'react'
import { DEFAULT_LOCALE, translate, type LocaleCode, type TranslateParams } from '@shared/i18n'
import { errorI18nKey, readIpcErrorPayload } from '@shared/errors'
import { useAppStore } from '../store/app-store'

const LOCALE: LocaleCode = DEFAULT_LOCALE

export type TFunction = (key: string, params?: TranslateParams) => string

export function useTranslation(): { t: TFunction; locale: LocaleCode } {
  // 订阅主题变化只是为了让组件在需要时重渲染；语言首版固定 zh-CN。
  useAppStore((s) => s.theme)
  const t = useCallback<TFunction>((key, params) => translate(LOCALE, key, params), [])
  return { t, locale: LOCALE }
}

/** 非 Hook 场景（store / 工具函数）使用。 */
export function t(key: string, params?: TranslateParams): string {
  return translate(LOCALE, key, params)
}

/**
 * 把任意异常转为可展示文案。
 *
 * 优先级：显式 i18nKey → 服务端/客户端错误码 → 通用兜底。
 * 这样 `40101`、`40201`、`VISION_INVALID_MIME` 等都能命中 §5.2/§7.0 的错误码表。
 */
export function describeError(err: unknown): string {
  if (!err) return translate(LOCALE, 'error.unknown')

  // 先解出 IPC 错误载荷（跨 contextBridge 后 code/i18nKey 只能从 message 里还原）
  const payload = readIpcErrorPayload(err)
  const anyErr = err as { message?: string }

  if (payload?.i18nKey && payload.i18nKey !== 'error.unknown') {
    // 主进程可能回传的是「设置项校验」这类非标准 key
    const localized = translate(LOCALE, payload.i18nKey)
    if (localized !== payload.i18nKey) return localized
  }

  if (payload?.appErrorCode) {
    const localized = translate(LOCALE, errorI18nKey(payload.appErrorCode))
    if (localized !== 'error.unknown') return localized
  }

  if (typeof payload?.code === 'number') {
    const localized = translate(LOCALE, errorI18nKey(payload.code))
    if (localized !== 'error.unknown') return localized
  }

  // 主进程可能直接把 i18n key 当 message 传回（见 ipc/register.ts 的 throw new Error(key)）
  const raw = payload?.message || anyErr.message
  if (raw) {
    const localized = translate(LOCALE, raw)
    if (localized !== raw) return localized
    if (raw === 'NOT_CONNECTED') return translate(LOCALE, 'chat.notConnected')
    if (raw === 'EMPTY_MESSAGE') return translate(LOCALE, 'error.emptyMessage')
    if (raw === 'offline') return translate(LOCALE, 'interaction.disabled.offline')
  }

  return translate(LOCALE, 'error.unknown')
}

/** 错误码（供调用方做分支，如 40204 需提示「积分已退回」）。 */
export function errorCodeOf(err: unknown): number | null {
  return readIpcErrorPayload(err)?.code ?? null
}

/** 供组件在依赖项中使用（避免每渲染新建）。 */
export function useErrorText(): (err: unknown) => string {
  return useMemo(() => describeError, [])
}
