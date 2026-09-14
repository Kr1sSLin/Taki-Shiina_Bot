/**
 * 设置页（FR-SET-1..10、FR-CFG-1..3、FR-NOTI-7、FR-PKG-7、FR-AUTH-7）。
 *
 * 所有可持久化项一律经 `useAppStore.updateSettings(patch)` → 主进程 `settings:update`，
 * 渲染端不直接落盘（FR-ARCH-2）。逐条 FR 落点见各分区注释。
 */

import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import type { AppSettings } from '@shared/protocol'
import type { AttachmentUsage } from '@shared/ipc'
import { Button, IconButton, Input, Modal, Segmented, Spinner, Switch } from '../../components/primitives'
import { IconCopy, IconDownload, IconFolder, IconLogout, IconRefresh, IconTrash } from '../../components/Icons'
import { ThemeReveal, useReducedMotion } from '../../components/visual'
import { useAppStore } from '../../store/app-store'
import { useAuthStore } from '../../store/auth-store'
import { describeError, useTranslation } from '../../i18n'
import { formatBytes } from '../../lib/format'

/* -------------------------------------------------------------------------- */
/* 模块级辅助                                                                    */
/* -------------------------------------------------------------------------- */

const THEME_OPTIONS: Array<{ value: AppSettings['theme']; labelKey: string }> = [
  { value: 'system', labelKey: 'settings.theme.system' },
  { value: 'light', labelKey: 'settings.theme.light' },
  { value: 'dark', labelKey: 'settings.theme.dark' }
]

const NOTIFICATION_OPTIONS: Array<{ key: keyof AppSettings['notifications']; labelKey: string }> = [
  { key: 'chat', labelKey: 'settings.notifications.chat' },
  { key: 'greeting', labelKey: 'settings.notifications.greeting' },
  { key: 'reminder', labelKey: 'settings.notifications.reminder' },
  { key: 'error', labelKey: 'settings.notifications.error' },
  { key: 'progress', labelKey: 'settings.notifications.progress' }
]

/** FR-CFG-2：仅回环地址可免二次确认。 */
const LOOPBACK_HOSTS = ['localhost', '127.0.0.1', '::1']

function parseTarget(raw: string): { protocol: string; host: string } | null {
  try {
    const url = new URL(raw.trim())
    return { protocol: url.protocol, host: url.hostname.replace(/^\[|\]$/g, '') }
  } catch {
    return null
  }
}

/** FR-CFG-2：非回环的 `http://` / `ws://` 需二次确认。 */
function isInsecureRemote(raw: string): boolean {
  const parsed = parseTarget(raw)
  if (!parsed) return false
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'ws:') return false
  return !LOOPBACK_HOSTS.includes(parsed.host.toLowerCase())
}

/** FR-SET-2：截断 `#` 之后的内容（对齐 Android `substringBefore('#')`）。 */
function cleanCity(value: string): string {
  return (value ?? '').split('#')[0].trim()
}

/** FR-SET-7：把键盘事件组装为 Electron accelerator（至少一个修饰键，主进程会再规范化）。 */
function buildAccelerator(event: ReactKeyboardEvent<HTMLInputElement>): string | null {
  const ignored = ['Control', 'Alt', 'Shift', 'Meta', 'CapsLock', 'Dead', 'Unidentified']
  if (ignored.includes(event.key)) return null

  const parts: string[] = []
  if (event.ctrlKey) parts.push('CommandOrControl')
  if (event.altKey) parts.push('Alt')
  if (event.shiftKey) parts.push('Shift')
  if (event.metaKey) parts.push('Super')
  // 单键全局快捷键容易误触，主进程同样拒绝（normalizeAccelerator）
  if (parts.length === 0) return null

  const arrow = /^Arrow(Up|Down|Left|Right)$/.exec(event.key)
  if (arrow) {
    parts.push(arrow[1])
  } else if (event.key === ' ') {
    parts.push('Space')
  } else if (event.key.length === 1) {
    parts.push(event.key.toUpperCase())
  } else {
    parts.push(event.key)
  }
  return parts.join('+')
}

/* -------------------------------------------------------------------------- */
/* 页面                                                                        */
/* -------------------------------------------------------------------------- */

export function SettingsPage(): JSX.Element {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const reducedMotion = useReducedMotion()

  const settings = useAppStore((s) => s.settings)
  const about = useAppStore((s) => s.about)
  const platform = useAppStore((s) => s.platform)
  const shortcut = useAppStore((s) => s.shortcut)
  const systemDark = useAppStore((s) => s.systemDark)
  const updateSettings = useAppStore((s) => s.updateSettings)
  const pushToast = useAppStore((s) => s.pushToast)

  const deviceId = useAuthStore((s) => s.deviceId)
  const storageMode = useAuthStore((s) => s.storageMode)

  const [apiUrl, setApiUrl] = useState('')
  const [wsUrl, setWsUrl] = useState('')
  const [city, setCity] = useState('')
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<string | null>(null)
  const [savingUrl, setSavingUrl] = useState(false)
  const [insecureConfirm, setInsecureConfirm] = useState(false)
  const [recording, setRecording] = useState(false)
  const [clearConfirm, setClearConfirm] = useState(false)
  const [logoutConfirm, setLogoutConfirm] = useState(false)
  const [logoutClearData, setLogoutClearData] = useState(false)
  const [usage, setUsage] = useState<AttachmentUsage | null>(null)
  const [busyKey, setBusyKey] = useState<string | null>(null)
  const [revealOrigin, setRevealOrigin] = useState<{ x: number; y: number } | null>(null)
  const [revealActive, setRevealActive] = useState(false)

  const shortcutInputRef = useRef<HTMLInputElement>(null)
  const revealTimerRef = useRef<number | null>(null)

  // 设置项在 store 首次填充后再回填受控输入（不在每次 store 变更时覆盖用户输入）
  useEffect(() => {
    if (!settings) return
    setApiUrl((current) => (current ? current : settings.apiBaseUrl))
    setWsUrl((current) => (current ? current : settings.wsBaseUrl))
    setCity((current) => (current ? current : settings.city))
  }, [settings])

  // FR-SET-10 / EDGE-L15：附件占用统计（进入设置页即拉一次）
  useEffect(() => {
    let alive = true
    void window.tks.images
      .usage()
      .then((value) => {
        if (alive) setUsage(value)
      })
      .catch(() => {
        if (alive) setUsage(null)
      })
    return () => {
      alive = false
    }
  }, [])

  useEffect(
    () => () => {
      if (revealTimerRef.current !== null) window.clearTimeout(revealTimerRef.current)
    },
    []
  )

  if (!settings) {
    return (
      <div className="page settings-page">
        <header className="page-head">
          <h1 className="page-title">{t('settings.title')}</h1>
        </header>
        <div className="page-body">
          <Spinner size={20} />
        </div>
      </div>
    )
  }

  /* ------------------------------- 通用动作 ------------------------------- */

  /** 设置项快照：优先读 store 当前值，避免闭包持有过期的 `settings`。 */
  const fallbackSettings = settings
  function currentSettings(): AppSettings {
    return useAppStore.getState().settings ?? fallbackSettings
  }

  /** 所有设置项统一经 store 持久化（FR-CFG-1）。 */
  async function persist(patch: Partial<AppSettings>): Promise<void> {
    try {
      await updateSettings(patch)
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  /* --------------------------- FR-SET-1 主题 + 揭示动画 --------------------------- */

  async function chooseTheme(theme: AppSettings['theme'], event: ReactMouseEvent<HTMLButtonElement>): Promise<void> {
    // 键盘触发 click 时 clientX/Y 为 0，回退到按钮中心，保证动画起点合理
    if (event.clientX === 0 && event.clientY === 0 && event.currentTarget) {
      const rect = event.currentTarget.getBoundingClientRect()
      setRevealOrigin({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 })
    } else {
      setRevealOrigin({ x: event.clientX, y: event.clientY })
    }

    // NFR-11：系统「减少动画」时直接切换，不播放揭示过渡
    if (!reducedMotion) {
      setRevealActive(true)
      if (revealTimerRef.current !== null) window.clearTimeout(revealTimerRef.current)
      revealTimerRef.current = window.setTimeout(() => setRevealActive(false), 700)
    }

    await persist({ theme })
  }

  const revealColor = settings.theme === 'dark' || (settings.theme === 'system' && systemDark) ? '#0d0f14' : '#f6f7fb'

  /* --------------------------- FR-SET-3 / FR-CFG-1/2 服务地址 --------------------------- */

  async function runConnectionTest(): Promise<void> {
    setTesting(true)
    setTestResult(null)
    try {
      const result = await window.tks.settings.testConnection(apiUrl.trim())
      setTestResult(
        result.ok ? t('settings.test.ok', { ms: result.latencyMs ?? 0 }) : t('settings.test.fail')
      )
    } catch (err) {
      setTestResult(describeError(err))
    } finally {
      setTesting(false)
    }
  }

  async function applyUrls(): Promise<void> {
    setSavingUrl(true)
    try {
      // FR-CFG-1：主进程会规范化地址并断开重连
      const next = await updateSettings({ apiBaseUrl: apiUrl.trim(), wsBaseUrl: wsUrl.trim() })
      setApiUrl(next.apiBaseUrl)
      setWsUrl(next.wsBaseUrl)
      pushToast({ level: 'success', i18nKey: 'settings.url.applied' })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setSavingUrl(false)
      setInsecureConfirm(false)
    }
  }

  function requestApplyUrls(): void {
    // FR-CFG-2：http:// / ws:// 且非回环时先二次确认
    if (isInsecureRemote(apiUrl) || isInsecureRemote(wsUrl)) {
      setInsecureConfirm(true)
      return
    }
    void applyUrls()
  }

  /* ------------------------------- FR-SET-2 城市 ------------------------------- */

  async function saveCity(): Promise<void> {
    const cleaned = cleanCity(city)
    setCity(cleaned)
    try {
      const saved = await window.tks.settings.setCity(cleaned)
      useAppStore.getState().setSettings({ ...currentSettings(), city: cleanCity(saved) })
      pushToast({ level: 'success', i18nKey: 'settings.city.saved' })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  /* ------------------------------- FR-SET-5 自启 ------------------------------- */

  async function changeAutostart(enabled: boolean, hidden: boolean): Promise<void> {
    setBusyKey('autostart')
    try {
      const ok = await window.tks.settings.setAutostart(enabled, hidden)
      if (!ok) {
        // FR-DSK-3：写入 `~/.config/autostart` 失败必须明示，不得静默
        pushToast({ level: 'error', i18nKey: 'settings.autostart.failed' })
        return
      }
      useAppStore.getState().setSettings({ ...currentSettings(), autostart: enabled, autostartHidden: hidden })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'settings.autostart.failed', text: describeError(err) })
    } finally {
      setBusyKey(null)
    }
  }

  /* --------------------------- FR-SET-7 全局快捷键 --------------------------- */

  async function applyShortcut(accelerator: string, enabled: boolean): Promise<void> {
    try {
      // `ShortcutUpdateResult`：主进程同时回传注册结果与最新状态（见 shared/ipc.ts）
      const reply = await window.tks.settings.setShortcut(accelerator, enabled)
      useAppStore.setState({ shortcut: reply.status })
      // 主进程已把值写入设置，回读一次保证渲染端镜像一致
      useAppStore.getState().setSettings(await window.tks.settings.get())

      const errorKey = reply.errorI18nKey
      if (errorKey) {
        const known =
          errorKey === 'settings.shortcut.conflict'
            ? 'settings.shortcut.conflict'
            : errorKey === 'settings.shortcut.waylandUnavailable'
              ? 'settings.shortcut.waylandUnavailable'
              : 'settings.shortcut.invalid'
        pushToast({ level: 'warn', i18nKey: known })
        return
      }
      pushToast({ level: 'success', i18nKey: 'settings.shortcut.saved', params: { accel: accelerator } })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  function onShortcutKeyDown(event: ReactKeyboardEvent<HTMLInputElement>): void {
    if (!recording) return
    event.preventDefault()
    const accelerator = buildAccelerator(event)
    if (!accelerator) {
      pushToast({ level: 'warn', i18nKey: 'settings.shortcut.invalid' })
      return
    }
    setRecording(false)
    void applyShortcut(accelerator, currentSettings().globalShortcutEnabled)
  }

  function toggleRecording(): void {
    const next = !recording
    setRecording(next)
    if (next) shortcutInputRef.current?.focus()
  }

  /* ------------------------------- FR-SET-4 数据 ------------------------------- */

  async function clearLocalConversation(): Promise<void> {
    setClearConfirm(false)
    try {
      // FR-CHAT-12：与聊天页的「清空会话」共用同一实现（会一并推进同步游标）
      const { deleted } = await window.tks.chat.clearConversation()
      pushToast({ level: 'success', i18nKey: 'settings.clearLocal.done', params: { n: deleted } })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  async function exportHistory(format: 'json' | 'text'): Promise<void> {
    setBusyKey(`export.${format}`)
    try {
      const result = await window.tks.settings.export(format)
      if (result.saved) {
        pushToast({ level: 'success', i18nKey: 'settings.export.done', params: { n: result.count } })
      } else {
        pushToast({ level: 'info', i18nKey: 'settings.export.cancelled' })
      }
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setBusyKey(null)
    }
  }

  async function refreshUsage(): Promise<void> {
    try {
      setUsage(await window.tks.images.usage())
    } catch {
      setUsage(null)
    }
  }

  async function cleanupAttachments(): Promise<void> {
    setBusyKey('cleanup')
    try {
      // EDGE-L15：清理 7 天前未被任何消息引用的草稿附件
      const result = await window.tks.images.cleanup(7 * 24 * 3600 * 1000)
      await refreshUsage()
      pushToast({ level: 'success', i18nKey: 'settings.cleanupAttachments.done', params: { n: result.removed } })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setBusyKey(null)
    }
  }

  async function openLogsDir(): Promise<void> {
    try {
      await window.tks.settings.openLogs()
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  async function openDataDirectory(): Promise<void> {
    try {
      await window.tks.settings.openDataDir()
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  /* ------------------------------- FR-SET-9 关于 ------------------------------- */

  async function copyDeviceId(): Promise<void> {
    const value = deviceId || about?.deviceId || ''
    if (!value || !navigator.clipboard?.writeText) return
    try {
      await navigator.clipboard.writeText(value)
      pushToast({ level: 'success', i18nKey: 'common.copied' })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  /* ------------------------------ FR-AUTH-7 退出登录 ------------------------------ */

  async function doLogout(): Promise<void> {
    setLogoutConfirm(false)
    try {
      await useAuthStore.getState().logout(logoutClearData)
      pushToast({ level: 'success', i18nKey: 'auth.logout.done' })
      navigate('/login', { replace: true })
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  /* --------------------------------- 派生展示 --------------------------------- */

  // EDGE-L8：`shortcut.available` 为主进程真实探测结果；store 尚未就绪时用会话类型兜底
  const shortcutUnavailable = shortcut ? !shortcut.available : platform?.sessionType === 'wayland'
  const shortcutValue = recording ? '' : (shortcut?.registered ?? settings.globalShortcut)
  const sessionType = platform?.sessionType ?? 'unknown'
  const sessionLabel =
    sessionType === 'wayland'
      ? t('settings.about.sessionType.wayland')
      : sessionType === 'x11'
        ? t('settings.about.sessionType.x11')
        : t('settings.about.sessionType.unknown')
  const credentialLabel =
    (about?.credentialStorage ?? storageMode) === 'safeStorage'
      ? t('auth.storage.safeStorage')
      : (about?.credentialStorage ?? storageMode) === 'plaintext-0600'
        ? t('auth.storage.plaintext')
        : t('auth.storage.none')

  const aboutRows: Array<{ label: string; value: string }> = about
    ? [
        { label: t('settings.about.version'), value: about.version },
        { label: t('settings.about.electron'), value: about.electronVersion },
        { label: t('settings.about.chrome'), value: about.chromeVersion },
        { label: t('settings.about.node'), value: about.nodeVersion },
        { label: t('settings.about.apiBaseUrl'), value: about.apiBaseUrl },
        { label: t('settings.about.wsBaseUrl'), value: about.wsBaseUrl },
        { label: t('settings.about.sessionType'), value: sessionLabel },
        { label: t('settings.about.storage'), value: credentialLabel },
        { label: t('settings.about.logsDir'), value: about.logsDir },
        { label: t('settings.about.dataDir'), value: about.dataDir },
        { label: t('settings.about.configDir'), value: about.configDir },
        { label: t('settings.about.dbPath'), value: about.databasePath },
        {
          label: t('settings.about.tray'),
          value: platform
            ? platform.trayAvailable
              ? t('settings.about.trayAvailable')
              : t('settings.about.trayUnavailable')
            : t('common.unknown')
        }
      ]
    : []

  return (
    <div className="page settings-page">
      <header className="page-head">
        <h1 className="page-title">{t('settings.title')}</h1>
      </header>

      <div className="page-body">
        {/* ------------------------------ 外观 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.appearance')}</h2>
          <div className="row">
            <span className="row-label">{t('settings.theme')}</span>
            <div className="row-control">
              {/* FR-SET-1：三选一；用显式按钮以便捕获点击坐标播放 ThemeReveal（FR-UI-4） */}
              <div className="segmented" role="radiogroup" aria-label={t('settings.theme')}>
                {THEME_OPTIONS.map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    role="radio"
                    aria-checked={settings.theme === option.value}
                    className={['segmented-item', settings.theme === option.value ? 'is-active' : '']
                      .filter(Boolean)
                      .join(' ')}
                    onClick={(event) => void chooseTheme(option.value, event)}
                  >
                    {t(option.labelKey)}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </section>

        {/* ------------------------------ 连接 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.connection')}</h2>
          <div className="row">
            <div className="row-control">
              <Input
                label={t('settings.apiBaseUrl')}
                type="url"
                inputMode="url"
                spellCheck={false}
                autoComplete="off"
                hint={t('settings.url.hint')}
                value={apiUrl}
                onChange={(e) => setApiUrl(e.target.value)}
              />
              <Input
                label={t('settings.wsBaseUrl')}
                type="url"
                inputMode="url"
                spellCheck={false}
                autoComplete="off"
                value={wsUrl}
                onChange={(e) => setWsUrl(e.target.value)}
              />
            </div>
          </div>
          <div className="row">
            <div className="row-control">
              <Button
                icon={<IconRefresh size={16} />}
                loading={testing}
                onClick={() => void runConnectionTest()}
              >
                {testing ? t('settings.test.testing') : t('settings.test')}
              </Button>
              <Button variant="primary" loading={savingUrl} onClick={requestApplyUrls}>
                {t('settings.url.apply')}
              </Button>
            </div>
          </div>
          {testResult ? (
            <p className="row-hint" role="status">
              {testResult}
            </p>
          ) : null}
        </section>

        {/* ------------------------------ 行为 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.behavior')}</h2>

          {/* FR-SET-2：天气城市 */}
          <div className="row">
            <div className="row-control">
              <Input
                label={t('settings.city')}
                type="text"
                placeholder={t('settings.city.placeholder')}
                hint={t('settings.city.hint')}
                value={city}
                onChange={(e) => setCity(e.target.value)}
              />
              <Button onClick={() => void saveCity()}>{t('common.save')}</Button>
            </div>
          </div>
          {city === 'auto_ip' ? <p className="row-hint">{t('settings.city.auto')}</p> : null}

          {/* FR-SET-5：开机自启 */}
          <Switch
            checked={settings.autostart}
            disabled={busyKey === 'autostart'}
            onChange={(value) => void changeAutostart(value, settings.autostartHidden)}
            label={t('settings.autostart')}
          />
          <Switch
            checked={settings.autostartHidden}
            disabled={busyKey === 'autostart' || !settings.autostart}
            onChange={(value) => void changeAutostart(settings.autostart, value)}
            label={t('settings.autostart.hidden')}
          />

          {/* FR-SET-6：关闭窗口行为 */}
          <div className="row">
            <span className="row-label">{t('settings.closeBehavior')}</span>
            <div className="row-control">
              <Segmented
                value={settings.closeBehavior}
                ariaLabel={t('settings.closeBehavior')}
                options={[
                  { value: 'tray', label: t('settings.closeBehavior.tray') },
                  { value: 'quit', label: t('settings.closeBehavior.quit') }
                ]}
                onChange={(value) => void persist({ closeBehavior: value })}
              />
            </div>
          </div>

          {/* FR-SET-7 / EDGE-L8：全局快捷键 */}
          {shortcutUnavailable ? (
            <p className="row-hint" role="status">
              {t('settings.shortcut.waylandUnavailable')}
            </p>
          ) : null}
          <div className="row">
            <span className="row-label">{t('settings.shortcut')}</span>
            <div className="row-control">
              <Input
                ref={shortcutInputRef}
                readOnly
                value={shortcutValue}
                placeholder={recording ? t('settings.shortcut.recording') : t('settings.shortcut.record')}
                disabled={shortcutUnavailable}
                aria-label={t('settings.shortcut')}
                onKeyDown={onShortcutKeyDown}
                onBlur={() => setRecording(false)}
              />
              <Button disabled={shortcutUnavailable} onClick={toggleRecording}>
                {recording ? t('settings.shortcut.recording') : t('settings.shortcut.record')}
              </Button>
            </div>
          </div>
          <Switch
            checked={settings.globalShortcutEnabled}
            disabled={shortcutUnavailable}
            onChange={(value) => void applyShortcut(shortcut?.registered ?? settings.globalShortcut, value)}
            label={t('settings.shortcut.enabled')}
          />

          {/* FR-SET-8：发送键 */}
          <div className="row">
            <span className="row-label">{t('settings.sendKey')}</span>
            <div className="row-control">
              <Segmented
                value={settings.sendKey}
                ariaLabel={t('settings.sendKey')}
                options={[
                  { value: 'enter', label: t('settings.sendKey.enter') },
                  { value: 'ctrl+enter', label: t('settings.sendKey.ctrlEnter') }
                ]}
                onChange={(value) => void persist({ sendKey: value })}
              />
            </div>
          </div>
        </section>

        {/* ------------------------------ 通知 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.notifications')}</h2>
          {/* FR-NOTI-7：五类通知分类开关 */}
          {NOTIFICATION_OPTIONS.map((option) => (
            <Switch
              key={option.key}
              checked={settings.notifications[option.key]}
              label={t(option.labelKey)}
              onChange={(value) =>
                void persist({ notifications: { ...settings.notifications, [option.key]: value } })
              }
            />
          ))}

          <Switch
            checked={settings.doNotDisturb.enabled}
            onChange={(value) =>
              void persist({ doNotDisturb: { ...settings.doNotDisturb, enabled: value } })
            }
            label={t('settings.dnd.enabled')}
            hint={t('settings.dnd.hint')}
          />
          <div className="row">
            <div className="row-control">
              <Input
                label={t('settings.dnd.start')}
                type="time"
                value={settings.doNotDisturb.start}
                disabled={!settings.doNotDisturb.enabled}
                onChange={(e) =>
                  void persist({ doNotDisturb: { ...settings.doNotDisturb, start: e.target.value } })
                }
              />
              <Input
                label={t('settings.dnd.end')}
                type="time"
                value={settings.doNotDisturb.end}
                disabled={!settings.doNotDisturb.enabled}
                onChange={(e) =>
                  void persist({ doNotDisturb: { ...settings.doNotDisturb, end: e.target.value } })
                }
              />
            </div>
          </div>
        </section>

        {/* ------------------------------ 数据 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.data')}</h2>

          {/* FR-SET-4 / FR-CHAT-12 */}
          <div className="row">
            <div className="row-control">
              <Button variant="danger" icon={<IconTrash size={16} />} onClick={() => setClearConfirm(true)}>
                {t('settings.clearLocal')}
              </Button>
            </div>
          </div>
          <p className="row-hint">{t('settings.clearLocal.hint')}</p>
          <p className="row-hint">{t('settings.clearLocal.sharedHint')}</p>

          {/* FR-SET-10：导出 */}
          <div className="row">
            <span className="row-label">{t('settings.export')}</span>
            <div className="row-control">
              <Button
                icon={<IconDownload size={16} />}
                loading={busyKey === 'export.json'}
                onClick={() => void exportHistory('json')}
              >
                {t('settings.export.json')}
              </Button>
              <Button
                icon={<IconDownload size={16} />}
                loading={busyKey === 'export.text'}
                onClick={() => void exportHistory('text')}
              >
                {t('settings.export.text')}
              </Button>
            </div>
          </div>

          {/* EDGE-L15：附件占用与清理 */}
          <div className="row">
            <span className="row-label">{t('settings.attachmentUsage')}</span>
            <div className="row-control">
              <span className="muted">
                {usage
                  ? t('settings.attachmentUsage.value', {
                      count: usage.count,
                      size: formatBytes(usage.bytes)
                    })
                  : t('common.loading')}
              </span>
              <Button
                loading={busyKey === 'cleanup'}
                onClick={() => void cleanupAttachments()}
              >
                {t('settings.cleanupAttachments')}
              </Button>
            </div>
          </div>

          <div className="row">
            <div className="row-control">
              <Button icon={<IconFolder size={16} />} onClick={() => void openLogsDir()}>
                {t('settings.openLogs')}
              </Button>
              <Button icon={<IconFolder size={16} />} onClick={() => void openDataDirectory()}>
                {t('settings.openDataDir')}
              </Button>
            </div>
          </div>
        </section>

        {/* ------------------------------ 关于 ------------------------------ */}
        <section className="section">
          <h2 className="section-title">{t('settings.section.about')}</h2>

          <div className="list">
            {/* FR-SET-9：版本号一律来自 about.version，不得硬编码 */}
            {aboutRows.map((row) => (
              <div className="list-row" key={row.label}>
                <div className="list-main">
                  <span className="list-title">{row.label}</span>
                  <span className="list-sub">{row.value}</span>
                </div>
              </div>
            ))}

            <div className="list-row">
              <div className="list-main">
                <span className="list-title">{t('settings.about.deviceId')}</span>
                <span className="list-sub">{deviceId || about?.deviceId || ''}</span>
              </div>
              <div className="list-actions">
                <IconButton label={t('common.copy')} onClick={() => void copyDeviceId()}>
                  <IconCopy size={16} />
                </IconButton>
              </div>
            </div>
          </div>

          {/* FR-PKG-7 / OQ-6：仅提示，不自动下载安装 */}
          <div className="row">
            <div className="row-control">
              <Button
                icon={<IconRefresh size={16} />}
                onClick={() => pushToast({ level: 'info', i18nKey: 'settings.about.updateHint' })}
              >
                {t('settings.about.updateCheck')}
              </Button>
            </div>
          </div>
          <p className="row-hint">{t('settings.about.updateHint')}</p>

          {/* FR-AUTH-7：退出登录（本地聊天记录默认保留） */}
          <Switch
            checked={logoutClearData}
            onChange={setLogoutClearData}
            label={t('auth.logout.clearData')}
          />
          <div className="row">
            <div className="row-control">
              <Button variant="danger" icon={<IconLogout size={16} />} onClick={() => setLogoutConfirm(true)}>
                {t('auth.logout')}
              </Button>
            </div>
          </div>
        </section>
      </div>

      {/* FR-UI-4：主题揭示动画覆盖层 */}
      <ThemeReveal
        active={revealActive}
        origin={revealOrigin ?? { x: 0, y: 0 }}
        color={revealColor}
      />

      {/* FR-SET-4：二次确认 */}
      <Modal
        open={clearConfirm}
        role="alertdialog"
        title={t('settings.clearLocal')}
        onClose={() => setClearConfirm(false)}
        footer={
          <>
            <Button onClick={() => setClearConfirm(false)}>{t('common.cancel')}</Button>
            <Button variant="danger" onClick={() => void clearLocalConversation()}>
              {t('common.confirm')}
            </Button>
          </>
        }
      >
        <p>{t('chat.clear.confirm')}</p>
      </Modal>

      {/* FR-CFG-2：非加密地址二次确认 */}
      <Modal
        open={insecureConfirm}
        role="alertdialog"
        title={t('settings.url.warn.title')}
        onClose={() => setInsecureConfirm(false)}
        footer={
          <>
            <Button onClick={() => setInsecureConfirm(false)}>{t('common.cancel')}</Button>
            <Button variant="danger" loading={savingUrl} onClick={() => void applyUrls()}>
              {t('common.confirm')}
            </Button>
          </>
        }
      >
        <p>{t('settings.url.warn.insecure')}</p>
      </Modal>

      {/* FR-AUTH-7：退出登录二次确认 */}
      <Modal
        open={logoutConfirm}
        role="alertdialog"
        title={t('auth.logout')}
        onClose={() => setLogoutConfirm(false)}
        footer={
          <>
            <Button onClick={() => setLogoutConfirm(false)}>{t('common.cancel')}</Button>
            <Button variant="danger" onClick={() => void doLogout()}>
              {t('common.confirm')}
            </Button>
          </>
        }
      >
        <p>{t('auth.logout.confirm')}</p>
      </Modal>
    </div>
  )
}

export default SettingsPage
