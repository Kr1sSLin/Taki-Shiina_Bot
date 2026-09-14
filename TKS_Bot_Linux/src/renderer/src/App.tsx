/**
 * 应用根组件：侧边导航 + 路由。
 *
 * 路由表见 `docs/RENDERER_CONTRACT.md` §5。
 * FR-ARCH-1：本组件只负责渲染；WS / 数据库 / Token 全在主进程，窗口隐藏不影响连接。
 */

import { useEffect, useMemo } from 'react'
import { HashRouter, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { useAppStore } from './store/app-store'
import { useAuthStore } from './store/auth-store'
import { useChatStore } from './store/chat-store'
import { useTranslation } from './i18n'
import { Celebration, Toaster } from './components/feedback'
import { IconChat, IconHistory, IconSettings, IconUser } from './components/Icons'
import LoginPage from './features/auth/LoginPage'
import ChatPage from './features/chat/ChatPage'
import HistoryPage from './features/history/HistoryPage'
import HistoryWindow from './features/history/HistoryWindow'
import ProfilePage from './features/profile/ProfilePage'
import PointsHistoryPage from './features/profile/PointsHistoryPage'
import MakeupCalendarPage from './features/profile/MakeupCalendarPage'
import LevelGuidePage from './features/profile/LevelGuidePage'
import SettingsPage from './features/settings/SettingsPage'
import RemindersPage from './features/settings/RemindersPage'
import { Spinner } from './components/primitives'

interface NavItem {
  to: string
  labelKey: string
  icon: JSX.Element
}

function SideNav(): JSX.Element {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const unread = useAppStore((s) => s.unreadNotifications)

  const items = useMemo<NavItem[]>(
    () => [
      { to: '/chat', labelKey: 'nav.chat', icon: <IconChat size={19} /> },
      { to: '/history', labelKey: 'nav.history', icon: <IconHistory size={19} /> },
      { to: '/profile', labelKey: 'nav.profile', icon: <IconUser size={19} /> },
      { to: '/settings', labelKey: 'nav.settings', icon: <IconSettings size={19} /> }
    ],
    []
  )

  return (
    <nav className="side-nav" aria-label={t('app.name')}>
      <div className="side-nav-brand" aria-hidden="true">
        <span role="img" aria-label="panda">
          🐼
        </span>
      </div>
      {items.map((item) => {
        // 子路由（如 /profile/ledger）也应高亮父项
        const active = location.pathname === item.to || location.pathname.startsWith(`${item.to}/`)
        return (
          <button
            key={item.to}
            type="button"
            className={['side-nav-item', active ? 'is-active' : ''].filter(Boolean).join(' ')}
            aria-current={active ? 'page' : undefined}
            onClick={() => navigate(item.to)}
          >
            {item.icon}
            <span className="side-nav-label">{t(item.labelKey)}</span>
            {item.to === '/history' && unread > 0 ? (
              <span className="side-nav-badge" aria-label={t('history.notifications.unread', { n: unread })}>
                {unread > 99 ? '99+' : unread}
              </span>
            ) : null}
          </button>
        )
      })}
      <div className="side-nav-spacer" />
    </nav>
  )
}

function Shell(): JSX.Element {
  const authenticated = useAuthStore((s) => s.authenticated)
  const location = useLocation()

  // 未登录时统一跳登录页（EDGE-L2 / EDGE-L3）
  if (!authenticated && location.pathname !== '/login') {
    return <Navigate to="/login" replace />
  }
  if (authenticated && location.pathname === '/login') {
    return <Navigate to="/chat" replace />
  }
  if (location.pathname === '/login') {
    return <LoginPage />
  }

  return (
    <div className="app-shell">
      <SideNav />
      <main className="app-main">
        <Routes>
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/history" element={<HistoryPage />} />
          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/profile/ledger" element={<PointsHistoryPage />} />
          <Route path="/profile/makeup" element={<MakeupCalendarPage />} />
          <Route path="/profile/levels" element={<LevelGuidePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/settings/reminders" element={<RemindersPage />} />
          <Route path="/window/facts" element={<HistoryWindow />} />
          <Route path="*" element={<Navigate to="/chat" replace />} />
        </Routes>
      </main>
    </div>
  )
}

function BootGate({ children }: { children: React.ReactNode }): JSX.Element {
  const ready = useAppStore((s) => s.ready)
  const authChecked = useAuthStore((s) => s.checked)
  const { t } = useTranslation()

  if (!ready || !authChecked) {
    return (
      <div className="boot-screen">
        <Spinner size={18} />
        <span>{t('app.loading')}</span>
      </div>
    )
  }
  return <>{children}</>
}

export default function App(): JSX.Element {
  const bootstrap = useAppStore((s) => s.bootstrap)
  const bootstrapAuth = useAuthStore((s) => s.bootstrap)
  const ready = useAppStore((s) => s.ready)
  const authChecked = useAuthStore((s) => s.checked)
  const chatLoaded = useChatStore((s) => s.loaded)
  const loadHistory = useChatStore((s) => s.load)

  useEffect(() => {
    // 渲染端启动序列：设置/平台信息 → 认证状态
    void (async () => {
      await bootstrap()
      await bootstrapAuth()
    })()
  }, [bootstrap, bootstrapAuth])

  useEffect(() => {
    // FR-SYNC-1：启动即从本地 SQLite 加载历史并上屏，**不阻塞等待网络**
    if (!ready || !authChecked || chatLoaded) return
    void loadHistory()
  }, [ready, authChecked, chatLoaded, loadHistory])

  return (
    <HashRouter>
      <BootGate>
        <Shell />
        <Toaster />
        <Celebration />
      </BootGate>
    </HashRouter>
  )
}
