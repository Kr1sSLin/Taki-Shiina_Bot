#!/usr/bin/env node
/** Capture the five primary renderer routes from a real Electron window. */
import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import WebSocket from 'ws'

const root = resolve(import.meta.dirname, '..')
const work = process.env.TKS_CAPTURE_WORK || join(root, '.ui-capture')
const output = join(work, 'screenshots')
const mockPort = Number(process.env.TKS_CAPTURE_MOCK_PORT ?? 8792)
const cdpPort = Number(process.env.TKS_CAPTURE_CDP_PORT ?? 9334)
if (process.env.TKS_CAPTURE_WORK) {
  if (existsSync(work)) throw new Error('指定的截图工作目录必须尚不存在')
} else rmSync(work, { recursive: true, force: true })
mkdirSync(output, { recursive: true })
for (const name of ['home', 'config', 'data', 'state']) {
  mkdirSync(join(work, name), { recursive: true })
}
writeFileSync(join(work, 'config', 'settings.json'), JSON.stringify({
  apiBaseUrl: `http://127.0.0.1:${mockPort}/api/v1/`,
  wsBaseUrl: `ws://127.0.0.1:${mockPort}`,
  theme: 'light',
  allowPlaintextCredentials: true
}))

const children = []
function launch(cmd, args, env = {}) {
  const child = spawn(cmd, args, {
    cwd: root,
    env: { ...process.env, ...env },
    stdio: ['ignore', 'pipe', 'pipe']
  })
  children.push(child)
  child.stdout.on('data', (data) => process.stdout.write(data))
  child.stderr.on('data', (data) => process.stderr.write(data))
  return child
}
function cleanup() {
  for (const child of children) child.kill('SIGTERM')
}
process.on('SIGINT', () => { cleanup(); process.exit(130) })
process.on('SIGTERM', () => { cleanup(); process.exit(143) })

class Cdp {
  constructor(url) { this.url = url; this.id = 0; this.pending = new Map() }
  async connect() {
    this.ws = new WebSocket(this.url)
    await new Promise((resolve, reject) => {
      this.ws.once('open', resolve)
      this.ws.once('error', reject)
    })
    this.ws.on('message', (bytes) => {
      const msg = JSON.parse(String(bytes))
      const pending = this.pending.get(msg.id)
      if (!pending) return
      this.pending.delete(msg.id)
      if (msg.error) pending.reject(new Error(msg.error.message))
      else pending.resolve(msg.result)
    })
  }
  send(method, params = {}) {
    const id = ++this.id
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject })
      this.ws.send(JSON.stringify({ id, method, params }))
      setTimeout(() => {
        if (this.pending.has(id)) {
          this.pending.delete(id)
          reject(new Error(`CDP timeout: ${method}`))
        }
      }, 30000)
    })
  }
  async eval(expression) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
      userGesture: true
    })
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.text)
    return result.result.value
  }
  close() { this.ws.close() }
}

async function waitFor(check, attempts = 80, interval = 250) {
  for (let i = 0; i < attempts; i += 1) {
    const value = await check().catch(() => null)
    if (value) return value
    await sleep(interval)
  }
  throw new Error('Timed out waiting for UI')
}

async function capture(cdp, route, name) {
  await cdp.eval(`location.hash = ${JSON.stringify(`#/${route}`)}`)
  await waitFor(() => cdp.eval(`location.hash.startsWith(${JSON.stringify(`#/${route}`)}) && !document.querySelector('.boot-screen')`))
  await sleep(650)
  const result = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false })
  const path = join(output, `${name}.png`)
  writeFileSync(path, Buffer.from(result.data, 'base64'))
  const title = await cdp.eval('document.title')
  console.log(`${name}: ${path} (${title})`)
}

try {
  launch('node', ['mock-server/server.mjs'], { PORT: String(mockPort) })
  await waitFor(async () => (await fetch(`http://127.0.0.1:${mockPort}/healthz`)).ok)

  const installedBin = process.env.TKS_INSTALLED_BIN
  launch(installedBin || join(root, 'node_modules/electron/dist', process.platform === 'win32' ? 'electron.exe' : 'electron'), [
    '--no-sandbox', '--disable-gpu', '--disable-gpu-compositing',
    '--disable-software-rasterizer', '--disable-dev-shm-usage',
    `--remote-debugging-port=${cdpPort}`,
    ...(installedBin ? [] : ['.'])
  ], {
    HOME: join(work, 'home'),
    XDG_CACHE_HOME: join(work, 'home', '.cache'),
    TKS_CONFIG_DIR: join(work, 'config'),
    TKS_DATA_DIR: join(work, 'data'),
    TKS_STATE_DIR: join(work, 'state')
  })

  const target = await waitFor(async () => {
    const list = await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json()
    return list.find((item) => item.type === 'page' && item.webSocketDebuggerUrl && /index\.html|localhost/.test(item.url))
  })
  const cdp = new Cdp(target.webSocketDebuggerUrl)
  await cdp.connect()
  await cdp.send('Page.enable')
  await cdp.send('Runtime.enable')
  await waitFor(() => cdp.eval('Boolean(window.tks && document.querySelector(".auth-form"))'))
  await capture(cdp, 'login', 'login')

  for (const [name, value] of [['username', 'kris'], ['password', 'taki']]) {
    await cdp.eval(`document.querySelector('input[name="${name}"]').focus()`)
    await cdp.send('Input.insertText', { text: value })
  }
  await cdp.eval('document.querySelector(".auth-form").requestSubmit()')
  await waitFor(() => cdp.eval('location.hash.startsWith("#/chat") && Boolean(document.querySelector(".app-shell"))'))
  if (process.env.TKS_CAPTURE_HISTORY === '1') {
    // 仅本机 Mock 使用独立合成登录，不读取应用凭据。
    const tokens = await (await fetch(`http://127.0.0.1:${mockPort}/api/v1/auth/login`, { method: 'POST',
      headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: 'kris', password: 'taki', deviceId: 'p2-capture' }) })).json()
    const headers = { 'content-type': 'application/json', authorization: `Bearer ${tokens.accessToken}` }
    for (const body of [{ kind: 'seed_history', count: 3350, timestamp: 1700000000000 },
      { kind: 'history_image', data: readFileSync(join(root, 'resources/icons/tks-128.png')).toString('base64') }]) {
      const result = await fetch(`http://127.0.0.1:${mockPort}/__test__/trigger`, { method: 'POST', headers, body: JSON.stringify(body) })
      if (!result.ok) throw new Error('历史 fixture 写入失败')
    }
    const synced = await cdp.eval('window.tks.sync.syncHistory(true)')
    if (!synced.ok) throw new Error('历史 GUI 同步失败')
    const inventory = await cdp.eval(`(async () => {
      const rows = []; let page = await window.tks.chat.listMessages({limit:500});
      while(page.length) { rows.push(...page); const first=page[0]; page=await window.tks.chat.loadOlder({limit:500,beforeTimestamp:first.timestamp,beforeMessageId:first.messageId}); }
      return {count:rows.length,unique:new Set(rows.map(x=>x.messageId)).size,image:rows.find(x=>x.messageId==='p2-history-image')?.attachments?.length};
    })()`)
    if (inventory.count !== 10051 || inventory.unique !== 10051 || inventory.image !== 1) throw new Error(`历史集合不完整：${JSON.stringify(inventory)}`)
    await cdp.eval('document.querySelector(\'[data-message-id="p2-history-image"]\').scrollIntoView({block:"center"})')
    await waitFor(() => cdp.eval('Boolean(document.querySelector(\'[data-message-id="p2-history-image"] img\')?.naturalWidth)'))
    await capture(cdp, 'chat', 'p2-history-image')
    console.log(`P2 GUI：${JSON.stringify(inventory)}`)
  }
  for (const [route, name] of [
    ['chat', 'chat'],
    ['history', 'history'],
    ['profile', 'profile'],
    ['settings', 'settings']
  ]) await capture(cdp, route, name)
  cdp.close()
} finally {
  cleanup()
}
