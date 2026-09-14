/**
 * 系统托盘（FR-DSK-1、FR-CONN-9、EDGE-L10）。
 *
 * 「常驻可达」（G2 / US-L1）的载体，对标 Android 前台服务通知。
 *
 * EDGE-L10：GNOME 默认移除了系统托盘。本模块在创建失败时返回 false，
 * 由 `WindowManager.effectiveCloseBehavior()` 把「关闭窗口行为」回退为「直接退出」，
 * 避免用户点了关闭后应用凭空消失且无法唤起。
 */

import { Menu, Tray, app } from 'electron'
import { IPC } from '@shared/ipc'
import type { ConnectionState } from '@shared/protocol'
import { t } from '../app/i18n'
import { createLogger } from '../app/logger'
import { trayIconPath } from './icons'

const log = createLogger('tray')

export interface TrayCallbacks {
  onToggleWindow: () => void
  onShowWindow: (focusInput: boolean) => void
  onNavigate: (route: string) => void
  onReconnect: () => void
  onQuit: () => void
}

export class TrayManager {
  private tray: Tray | null = null
  private callbacks: TrayCallbacks
  private state: ConnectionState = { status: 'unauthenticated', attempt: 0, lastError: null, degraded: false }
  private unread = 0
  private created = false

  constructor(callbacks: TrayCallbacks) {
    this.callbacks = callbacks
  }

  /**
   * @returns 托盘是否创建成功（EDGE-L10 判定依据）
   */
  create(): boolean {
    if (this.tray) return this.created
    try {
      const icon = this.currentIconPath()
      this.tray = new Tray(icon)
      this.tray.setToolTip(t('tray.tooltip', { app: t('app.name'), status: this.statusLabel() }))

      // 左键点击：toggle 语义（FR-DSK-2 的对等体验）
      this.tray.on('click', () => this.callbacks.onToggleWindow())

      this.rebuildMenu()
      this.created = true
      log.info('托盘已创建')
      return true
    } catch (err) {
      this.created = false
      this.tray = null
      log.warn('托盘创建失败（EDGE-L10：GNOME 默认不显示系统托盘）', { error: String(err) })
      return false
    }
  }

  isAvailable(): boolean {
    return this.created && !!this.tray && !this.tray.isDestroyed()
  }

  /** FR-CONN-9：托盘图标以颜色区分在线与离线。 */
  updateConnection(state: ConnectionState): void {
    this.state = state
    if (!this.tray || this.tray.isDestroyed()) return
    try {
      this.tray.setImage(this.currentIconPath())
      this.tray.setToolTip(t('tray.tooltip', { app: t('app.name'), status: this.statusLabel() }))
      this.rebuildMenu()
    } catch (err) {
      log.debug('托盘刷新失败', { error: String(err) })
    }
  }

  /** FR-DSK-1：有未读消息时图标叠加角标。 */
  setUnread(count: number): void {
    this.unread = Math.max(0, count)
    if (!this.tray || this.tray.isDestroyed()) return
    try {
      this.tray.setImage(this.currentIconPath())
      this.rebuildMenu()
    } catch {
      /* ignore */
    }
  }

  private currentIconPath(): string {
    const state: 'online' | 'offline' | 'unread' =
      this.unread > 0 ? 'unread' : this.state.status === 'connected' ? 'online' : 'offline'
    return trayIconPath(state)
  }

  private statusLabel(): string {
    switch (this.state.status) {
      case 'connected':
        return t('conn.status.connected')
      case 'connecting':
        return t('conn.status.connecting')
      case 'reconnecting':
        return t('conn.status.reconnecting')
      case 'refreshing':
        return t('conn.status.refreshing')
      case 'degraded':
        return t('conn.status.degraded')
      case 'unauthenticated':
        return t('conn.status.unauthenticated')
      default:
        return t('conn.status.disconnected')
    }
  }

  private rebuildMenu(): void {
    if (!this.tray || this.tray.isDestroyed()) return

    const template: Electron.MenuItemConstructorOptions[] = [
      {
        label: t('tray.tooltip', { app: t('app.name'), status: this.statusLabel() }),
        enabled: false
      },
      { type: 'separator' },
      { label: t('tray.show'), click: () => this.callbacks.onShowWindow(false) },
      { label: t('tray.chat'), click: () => this.callbacks.onShowWindow(true) },
      { label: t('tray.profile'), click: () => this.callbacks.onNavigate('/profile') },
      { label: t('tray.reminders'), click: () => this.callbacks.onNavigate('/settings/reminders') },
      { type: 'separator' },
      {
        // FR-CONN-5：断开时提供「重新连接」
        label: t('conn.reconnect'),
        enabled: this.state.status !== 'connected',
        click: () => this.callbacks.onReconnect()
      },
      { label: t('tray.settings'), click: () => this.callbacks.onNavigate('/settings') },
      { type: 'separator' },
      { label: t('tray.quit'), click: () => this.callbacks.onQuit() }
    ]

    if (this.unread > 0) {
      template.splice(1, 0, { label: t('tray.unread', { n: this.unread }), enabled: false })
    }

    try {
      this.tray.setContextMenu(Menu.buildFromTemplate(template))
    } catch (err) {
      log.debug('托盘菜单构建失败', { error: String(err) })
    }
  }

  /** 供渲染进程同步未读（FR-NOTI-5 联动）。 */
  static badgeUpdateChannel(): string {
    return IPC.desktopSetBadge
  }

  destroy(): void {
    if (this.tray && !this.tray.isDestroyed()) {
      try {
        this.tray.destroy()
      } catch {
        /* ignore */
      }
    }
    this.tray = null
    this.created = false
    log.info('托盘已销毁')
  }

  /** 退出前置：避免托盘阻止进程退出。 */
  static beforeQuit(tray: TrayManager): void {
    tray.destroy()
    app.quit()
  }
}
