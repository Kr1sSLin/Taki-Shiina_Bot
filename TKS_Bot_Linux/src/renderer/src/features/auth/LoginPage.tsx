/**
 * 登录页（FR-AUTH-1 / FR-AUTH-8 / FR-AUTH-9；EDGE-L2 / EDGE-L3 / EDGE-L11）。
 *
 * - FR-AUTH-9：用户名从 `useAuthStore.lastUsername` 预填（只记用户名，不记密码）
 * - FR-AUTH-8：登录失败按后端错误码给出明确文案 —— 40101 → `error.api.40101`
 *   （用户名或密码错误）、40301 → `error.api.40301`（设备不在白名单）、
 *   40302 → `error.api.40302`（死代码兜底文案，见 EDGE-L3）
 * - EDGE-L11：`safeStorage` 不可用时给**非阻塞**的降级询问（明文 0600 / 不保存）
 * - EDGE-L2 / EDGE-L3：`expiryNotice` 横幅区分「被静默顶号」与「登录过期」
 */

import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { Button, Input } from '../../components/primitives'
import { IconWarning } from '../../components/Icons'
import { useAuthStore } from '../../store/auth-store'
import { describeError, useTranslation } from '../../i18n'

export function LoginPage(): JSX.Element {
  const { t } = useTranslation()
  const navigate = useNavigate()

  const lastUsername = useAuthStore((s) => s.lastUsername)
  const storageMode = useAuthStore((s) => s.storageMode)
  const storagePromptDismissed = useAuthStore((s) => s.storagePromptDismissed)
  const expiryNotice = useAuthStore((s) => s.expiryNotice)
  const setExpiryNotice = useAuthStore((s) => s.setExpiryNotice)
  const setStoragePromptDismissed = useAuthStore((s) => s.setStoragePromptDismissed)

  const [username, setUsername] = useState(lastUsername)
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [storageBusy, setStorageBusy] = useState(false)

  // bootstrap() 是异步的：lastUsername 可能在挂载之后才到达（FR-AUTH-9）
  useEffect(() => {
    if (!lastUsername) return
    setUsername((current) => (current ? current : lastUsername))
  }, [lastUsername])

  const storageHint =
    storageMode === 'safeStorage'
      ? t('auth.storage.safeStorage')
      : storageMode === 'plaintext-0600'
        ? t('auth.storage.plaintext')
        : t('auth.storage.none')

  // EDGE-L11：仅在密钥环不可用且用户尚未做出选择时提示
  const showStoragePrompt = storageMode === 'none' && !storagePromptDismissed

  async function onSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setError(null)
    if (!username.trim() || !password) {
      setError(t('auth.error.empty'))
      return
    }
    setSubmitting(true)
    try {
      await useAuthStore.getState().login(username.trim(), password)
      navigate('/chat', { replace: true })
    } catch (err) {
      // FR-AUTH-8：错误码 → 文案统一由 describeError 落地（40101 / 40301 / 40302…）
      setError(describeError(err))
    } finally {
      setSubmitting(false)
    }
  }

  /** EDGE-L11：接受明文（0600）降级保存。 */
  async function acceptPlaintextStorage(): Promise<void> {
    setStorageBusy(true)
    setError(null)
    try {
      await window.tks.settings.update({ allowPlaintextCredentials: true })
      const mode = await window.tks.auth.storageMode()
      useAuthStore.setState({ storageMode: mode })
      setStoragePromptDismissed(true)
    } catch (err) {
      setError(describeError(err))
    } finally {
      setStorageBusy(false)
    }
  }

  /** EDGE-L11：拒绝保存凭据，每次启动重新登录。 */
  async function rejectPlaintextStorage(): Promise<void> {
    setStorageBusy(true)
    setError(null)
    try {
      await window.tks.settings.update({ allowPlaintextCredentials: false })
    } catch (err) {
      setError(describeError(err))
    } finally {
      setStorageBusy(false)
      setStoragePromptDismissed(true)
    }
  }

  return (
    <div className="page auth-page">
      <div className="auth-card glass">
        <h1 className="auth-title">{t('auth.title')}</h1>
        <p className="auth-subtitle">{t('app.tagline')}</p>

        {/* EDGE-L3 / EDGE-L2：被静默顶号优先于「登录已过期」 */}
        {expiryNotice ? (
          <div className="auth-notice" role="alert">
            <p className="row-label">
              <IconWarning size={18} /> {expiryNotice.kicked ? t('auth.kicked') : t('auth.expired')}
            </p>
            <div className="auth-notice-actions">
              <Button variant="ghost" size="sm" onClick={() => setExpiryNotice(null)}>
                {t('common.close')}
              </Button>
            </div>
          </div>
        ) : null}

        {/* EDGE-L11：非阻塞提示，不遮挡登录表单 */}
        {showStoragePrompt ? (
          <div className="auth-notice" role="status">
            <p className="row-label">
              <IconWarning size={18} /> {t('auth.error.storageUnavailable.title')}
            </p>
            <p className="row-hint">{t('auth.error.storageUnavailable.body')}</p>
            <div className="auth-notice-actions">
              <Button variant="primary" size="sm" loading={storageBusy} onClick={() => void acceptPlaintextStorage()}>
                {t('auth.error.storageUnavailable.accept')}
              </Button>
              <Button variant="ghost" size="sm" disabled={storageBusy} onClick={() => void rejectPlaintextStorage()}>
                {t('auth.error.storageUnavailable.reject')}
              </Button>
            </div>
          </div>
        ) : null}

        <form className="auth-form" onSubmit={(event) => void onSubmit(event)}>
          <Input
            label={t('auth.username')}
            type="text"
            name="username"
            autoComplete="username"
            placeholder={t('auth.username.placeholder')}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            disabled={submitting}
          />
          <Input
            label={t('auth.password')}
            type="password"
            name="password"
            autoComplete="current-password"
            placeholder={t('auth.password.placeholder')}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            disabled={submitting}
          />

          {error ? (
            <p className="field-error" role="alert">
              {error}
            </p>
          ) : null}

          <Button type="submit" variant="primary" size="lg" block loading={submitting}>
            {submitting ? t('auth.submitting') : t('auth.submit')}
          </Button>
        </form>

        <p className="auth-storage-hint">{storageHint}</p>
      </div>
    </div>
  )
}

export default LoginPage
