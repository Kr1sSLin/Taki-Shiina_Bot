/// <reference types="vite/client" />

import type { TksApi } from '@shared/ipc'

declare global {
  interface Window {
    /** preload 通过 contextBridge 暴露的白名单 API（FR-ARCH-2）。 */
    tks: TksApi
  }
}

export {}
