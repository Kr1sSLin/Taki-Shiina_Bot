/**
 * 主进程 → 渲染进程 事件总线。
 *
 * 所有窗口（含多窗口 FR-DSK-8）都会收到广播；窗口已销毁时静默跳过。
 */

import { BrowserWindow } from 'electron'
import { createLogger } from './logger'

const log = createLogger('bus')

export interface RendererBus {
  send(channel: string, payload?: unknown): void
}

class Bus implements RendererBus {
  send(channel: string, payload?: unknown): void {
    for (const win of BrowserWindow.getAllWindows()) {
      if (win.isDestroyed()) continue
      try {
        win.webContents.send(channel, payload)
      } catch (err) {
        log.debug('事件投递失败', { channel, error: String(err) })
      }
    }
  }
}

export const bus: RendererBus = new Bus()
