/**
 * 认证状态（FR-AUTH-1..9）。
 */

import { create } from 'zustand'
import type { AuthTokens } from '@shared/protocol'

type StorageMode = 'safeStorage' | 'plaintext-0600' | 'none'

export interface AuthState {
  checked: boolean
  authenticated: boolean
  userId: string | null
  deviceId: string
  storageMode: StorageMode
  lastUsername: string
  /** EDGE-L11：密钥环不可用时的降级询问。 */
  storagePromptDismissed: boolean
  /** EDGE-L3：被静默顶号的提示。 */
  expiryNotice: { reason: string; kicked: boolean } | null

  bootstrap: () => Promise<void>
  login: (username: string, password: string) => Promise<AuthTokens>
  logout: (clearLocalData: boolean) => Promise<void>
  setStoragePromptDismissed: (v: boolean) => void
  setExpiryNotice: (notice: { reason: string; kicked: boolean } | null) => void
}

export const useAuthStore = create<AuthState>()((set, get) => ({
  checked: false,
  authenticated: false,
  userId: null,
  deviceId: '',
  storageMode: 'none',
  lastUsername: '',
  storagePromptDismissed: false,
  expiryNotice: null,

  async bootstrap() {
    const [status, storageMode, settings] = await Promise.all([
      window.tks.auth.status(),
      window.tks.auth.storageMode(),
      window.tks.settings.get()
    ])
    set({
      checked: true,
      authenticated: status.authenticated,
      userId: status.userId,
      deviceId: status.deviceId,
      storageMode,
      lastUsername: settings.lastUsername
    })
  },

  async login(username, password) {
    const tokens = await window.tks.auth.login(username, password)
    set({
      authenticated: true,
      userId: tokens.userId,
      deviceId: tokens.deviceId,
      lastUsername: username,
      expiryNotice: null
    })
    return tokens
  },

  async logout(clearLocalData) {
    await window.tks.auth.logout(clearLocalData)
    set({ authenticated: false, userId: null, expiryNotice: null })
  },

  setStoragePromptDismissed(storagePromptDismissed) {
    set({ storagePromptDismissed })
  },

  setExpiryNotice(expiryNotice) {
    set({ expiryNotice, authenticated: expiryNotice ? false : get().authenticated })
  }
}))
