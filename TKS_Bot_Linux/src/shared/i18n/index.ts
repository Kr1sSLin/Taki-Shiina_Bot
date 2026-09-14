/**
 * i18n 运行时（NFR-10）。
 *
 * 主进程与渲染进程共用；渲染进程额外通过 `useTranslation()` 订阅语言变化。
 * 首版仅提供简体中文（zh-CN）。
 */

import { zhCN, type LocaleDict, type LocaleKey } from './zh-CN'

export type LocaleCode = 'zh-CN'

export const LOCALES: Record<LocaleCode, LocaleDict> = {
  'zh-CN': zhCN as LocaleDict
}

export const DEFAULT_LOCALE: LocaleCode = 'zh-CN'

const DICTS: Record<LocaleCode, LocaleDict> = LOCALES

export type TranslateParams = Record<string, string | number>

/**
 * 取文案并做 `{name}` 插值。
 * 找不到 key 时返回 key 本身（便于开发期发现遗漏），不抛错。
 */
export function translate(locale: LocaleCode, key: string, params?: TranslateParams): string {
  const dict = DICTS[locale] ?? DICTS[DEFAULT_LOCALE]
  const template = (dict as Record<string, string | undefined>)[key]
  if (template === undefined) return key
  if (!params) return template
  return template.replace(/\{(\w+)\}/g, (match, name: string) => {
    const value = params[name]
    return value === undefined ? match : String(value)
  })
}

export type { LocaleKey, LocaleDict }
export { zhCN }
