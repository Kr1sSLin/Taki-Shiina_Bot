/**
 * Token 与 deviceId 安全存储（FR-AUTH-2 / FR-AUTH-3 / EDGE-L11）。
 *
 * - 首选 Electron `safeStorage`（Linux 后端为 libsecret / GNOME Keyring、kwallet）。
 * - 不可用时（无 keyring / headless）：明确告知用户，**用户确认后**才降级为
 *   `~/.config/tks-desktop/credentials.json`（`chmod 0600`）明文存储；
 *   用户拒绝则不持久化 Token，每次启动需重新登录。
 * - 禁止明文存储、禁止写入 `localStorage`（FR-AUTH-3）。
 */

import { randomUUID } from 'node:crypto'
import { existsSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { safeStorage } from 'electron'
import type { AuthTokens } from '@shared/protocol'
import { createLogger } from '../../app/logger'
import { getSettings } from '../../app/config-store'
import { ensureDirs, paths } from '../../app/paths'

const log = createLogger('auth:tokens')

export type CredentialStorageMode = 'safeStorage' | 'plaintext-0600' | 'none'

interface StoredCredentials {
  accessToken: string
  refreshToken: string
  userId: string
  deviceId: string
  /** Access Token 绝对过期时刻（毫秒），用于提前续签。 */
  accessTokenExpiresAt: number
}

const ENCRYPTED_FILE = 'credentials.enc'
const PLAINTEXT_FILE = 'credentials.json'

let cache: StoredCredentials | null = null
let loaded = false

function encryptedPath(): string {
  return join(paths().configDir, ENCRYPTED_FILE)
}

function plaintextPath(): string {
  return join(paths().configDir, PLAINTEXT_FILE)
}

/** EDGE-L11：探测系统密钥环是否可用。 */
export function isSafeStorageAvailable(): boolean {
  try {
    return safeStorage.isEncryptionAvailable()
  } catch {
    return false
  }
}

export function credentialStorageMode(): CredentialStorageMode {
  if (isSafeStorageAvailable()) return 'safeStorage'
  if (getSettings().allowPlaintextCredentials) return 'plaintext-0600'
  return 'none'
}

function atomicWrite(file: string, data: string | Buffer): void {
  ensureDirs()
  const tmp = join(dirname(file), `.${process.pid}.${Date.now()}.tmp`)
  writeFileSync(tmp, data, { mode: 0o600 })
  renameSync(tmp, file)
}

function load(): StoredCredentials | null {
  if (loaded) return cache
  loaded = true

  // ① safeStorage 密文
  if (isSafeStorageAvailable() && existsSync(encryptedPath())) {
    try {
      const buf = readFileSync(encryptedPath())
      const plain = safeStorage.decryptString(buf)
      cache = JSON.parse(plain) as StoredCredentials
      log.info('已从 safeStorage 载入凭据')
      return cache
    } catch (err) {
      log.warn('safeStorage 解密失败，凭据作废', { error: String(err) })
      try {
        unlinkSync(encryptedPath())
      } catch {
        /* ignore */
      }
      cache = null
    }
  }

  // ② 用户确认的 0600 明文降级
  if (existsSync(plaintextPath())) {
    if (!getSettings().allowPlaintextCredentials) {
      log.warn('检测到明文凭据文件，但用户未同意降级存储，已忽略')
      return null
    }
    try {
      cache = JSON.parse(readFileSync(plaintextPath(), 'utf8')) as StoredCredentials
      log.warn('已从 0600 明文文件载入凭据（降级模式）')
      return cache
    } catch (err) {
      log.warn('明文凭据解析失败', { error: String(err) })
      cache = null
    }
  }

  return cache
}

/** 返回实际生效的存储模式；`none` 表示未持久化。 */
function persist(creds: StoredCredentials | null): CredentialStorageMode {
  cache = creds
  loaded = true

  if (!creds) {
    for (const file of [encryptedPath(), plaintextPath()]) {
      if (existsSync(file)) {
        try {
          unlinkSync(file)
        } catch (err) {
          log.warn('删除凭据文件失败', { file, error: String(err) })
        }
      }
    }
    return 'none'
  }

  if (isSafeStorageAvailable()) {
    try {
      atomicWrite(encryptedPath(), safeStorage.encryptString(JSON.stringify(creds)))
      // 升级到安全存储后清掉遗留明文
      if (existsSync(plaintextPath())) unlinkSync(plaintextPath())
      return 'safeStorage'
    } catch (err) {
      log.error('safeStorage 加密写入失败', { error: String(err) })
    }
  }

  if (getSettings().allowPlaintextCredentials) {
    atomicWrite(plaintextPath(), `${JSON.stringify(creds, null, 2)}\n`)
    log.warn('凭据以 0600 明文存储（EDGE-L11 降级模式）')
    return 'plaintext-0600'
  }

  log.warn('密钥环不可用且用户未同意明文降级：本次运行不持久化凭据')
  return 'none'
}

/* -------------------------------------------------------------------------- */
/* deviceId（FR-AUTH-2）                                                        */
/* -------------------------------------------------------------------------- */

const DEVICE_FILE = 'device.json'

/**
 * 生成并持久化 `device_{uuidv4}`，此后所有登录复用同一 ID。
 * 对标 Android `TokenManager.getOrCreateDeviceId()`。
 *
 * deviceId 本身不是凭据，但为保持一致性仍优先存入安全存储；
 * 密钥环不可用时落 `device.json`（0600，非机密）以保证设备身份稳定。
 */
export function getOrCreateDeviceId(): string {
  const creds = load()
  if (creds?.deviceId) return creds.deviceId

  const deviceFile = join(paths().configDir, DEVICE_FILE)
  if (existsSync(deviceFile)) {
    try {
      const parsed = JSON.parse(readFileSync(deviceFile, 'utf8')) as { deviceId?: string }
      if (parsed.deviceId) return parsed.deviceId
    } catch {
      /* 损坏则重新生成 */
    }
  }

  const deviceId = `device_${randomUUID()}`
  try {
    atomicWrite(deviceFile, `${JSON.stringify({ deviceId }, null, 2)}\n`)
  } catch (err) {
    log.warn('deviceId 持久化失败，本次运行使用临时 ID', { error: String(err) })
  }
  log.info('已生成新的 deviceId')
  return deviceId
}

/* -------------------------------------------------------------------------- */
/* Token 读写                                                                   */
/* -------------------------------------------------------------------------- */

export function getStoredTokens(): StoredCredentials | null {
  return load()
}

export function getAccessToken(): string | null {
  return load()?.accessToken ?? null
}

export function getRefreshToken(): string | null {
  return load()?.refreshToken ?? null
}

export function getUserId(): string | null {
  return load()?.userId ?? null
}

export function isAuthenticated(): boolean {
  return !!load()?.refreshToken
}

/** Access Token 是否已过期或即将在 `skewMs` 内过期（默认 60s 提前量）。 */
export function isAccessTokenExpired(skewMs = 60_000): boolean {
  const creds = load()
  if (!creds) return true
  return creds.accessTokenExpiresAt - skewMs <= Date.now()
}

export function storeTokens(tokens: AuthTokens, deviceId: string): CredentialStorageMode {
  const expiresAt = Date.now() + Math.max(0, tokens.expiresIn ?? 900) * 1000
  return persist({
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    userId: tokens.userId,
    deviceId: tokens.deviceId || deviceId,
    accessTokenExpiresAt: expiresAt
  })
}

export function clearTokens(): void {
  persist(null)
  log.info('已清除本地凭据')
}

export function accessTokenExpiresAt(): number | null {
  return load()?.accessTokenExpiresAt ?? null
}
