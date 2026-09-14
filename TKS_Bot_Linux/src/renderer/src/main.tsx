/**
 * 渲染进程入口。
 *
 * NFR-6：不在渲染端持久化任何 Token（由主进程 safeStorage 负责）。
 */

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { wireEvents } from './store/events'
import './styles/index.css'

const container = document.getElementById('root')
if (!container) throw new Error('#root not found')

// 事件订阅先于渲染建立：避免「连接已建立/消息已到达」这类早发事件被丢掉
// （Android 端曾因订阅晚于事件而丢失 `Connected`，见 FR-SYNC-2 的说明）
wireEvents()

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>
)
