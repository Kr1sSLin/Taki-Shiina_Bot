#!/usr/bin/env node
/**
 * 无界面集成自检的驱动器。
 *
 * 自动完成：起 Mock 后端（若指定 `SELFTEST_USE_MOCK=1` 或未提供真实地址）→
 * 在真实 Electron 主进程里跑 `out/main/selftest.js` → 汇总退出码。
 *
 * 该模式不创建窗口、不加载渲染进程，因此可在无显示器 / 无 GPU /
 * Chromium 渲染子进程无法启动的受限环境（CI 容器、bwrap 沙箱）里运行。
 */

import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'

const ROOT = resolve(import.meta.dirname, '..')
const MOCK_PORT = Number(process.env.SELFTEST_MOCK_PORT ?? 8792)
const useMock = !process.env.TKS_SELFTEST_API

const children = []
function killAll() {
  for (const c of children) {
    try {
      c.kill('SIGTERM')
    } catch {
      /* ignore */
    }
  }
}

async function main() {
  const entry = join(ROOT, 'out/main/selftest.js')
  if (!existsSync(entry)) {
    console.error(`未找到 ${entry}，先执行 npm run build 或 electron-vite build`)
    return 1
  }

  let api = process.env.TKS_SELFTEST_API
  let ws = process.env.TKS_SELFTEST_WS

  if (useMock) {
    api = `http://127.0.0.1:${MOCK_PORT}/api/v1/`
    ws = `ws://127.0.0.1:${MOCK_PORT}`
    const mock = spawn('node', ['mock-server/server.mjs'], {
      cwd: ROOT,
      env: { ...process.env, PORT: String(MOCK_PORT), DEBOUNCE_MS: '600', MERGE_WAIT_MS: '250' },
      stdio: ['ignore', 'pipe', 'pipe']
    })
    children.push(mock)
    mock.stdout.setEncoding('utf8')
    mock.stderr.setEncoding('utf8')
    if (process.env.SELFTEST_VERBOSE === '1') {
      mock.stdout.on('data', (d) => process.stdout.write(`\u001b[90m[mock] ${d}\u001b[0m`))
      mock.stderr.on('data', (d) => process.stdout.write(`\u001b[90m[mock] ${d}\u001b[0m`))
    }
    let up = false
    for (let i = 0; i < 40; i += 1) {
      try {
        const res = await fetch(`http://127.0.0.1:${MOCK_PORT}/healthz`)
        if (res.status === 200) {
          up = true
          break
        }
      } catch {
        /* retry */
      }
      await sleep(250)
    }
    if (!up) {
      console.error('Mock 后端启动失败')
      return 1
    }
  }

  // Windows 上 Electron 的可执行文件名为 electron.exe（Linux/macOS 无后缀）
  const electronBin = join(ROOT, 'node_modules/electron/dist', process.platform === 'win32' ? 'electron.exe' : 'electron')
  if (!existsSync(electronBin)) {
    console.error('未找到 Electron 可执行文件，请先执行 npm install')
    return 1
  }

  const code = await new Promise((resolveCode) => {
    const child = spawn(electronBin, ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', entry], {
      cwd: ROOT,
      env: {
        ...process.env,
        TKS_SELFTEST_API: api,
        TKS_SELFTEST_WS: ws,
        LIBGL_ALWAYS_SOFTWARE: '1'
      },
      stdio: 'inherit'
    })
    children.push(child)
    child.on('exit', (c) => resolveCode(c ?? 1))
  })

  return code
}

let code = 1
try {
  code = await main()
} catch (err) {
  console.error(`自检驱动异常：${err.message}`)
  code = 1
} finally {
  killAll()
  await sleep(300)
}
process.exit(code)
