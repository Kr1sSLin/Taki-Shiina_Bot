/**
 * 主进程侧 i18n 便捷函数（托盘菜单、桌面通知、日志内的用户可见文案）。
 */

import { DEFAULT_LOCALE, translate, type LocaleCode, type TranslateParams } from '@shared/i18n'

let current: LocaleCode = DEFAULT_LOCALE

export function setMainLocale(locale: LocaleCode): void {
  current = locale
}

export function t(key: string, params?: TranslateParams): string {
  return translate(current, key, params)
}
