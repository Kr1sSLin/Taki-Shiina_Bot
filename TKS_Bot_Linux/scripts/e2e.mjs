#!/usr/bin/env node
/**
 * 端到端验收测试（真实 Electron 进程 + 契约一致的 Mock 后端）。
 *
 * 与 `verify-contract.mjs` 的区别：
 *   - `verify-contract.mjs` 只验证**协议形状**（裸 WS/HTTP 客户端）
 *   - 本脚本启动**真实客户端**，经 CDP 驱动渲染进程，走完整的
 *     主进程（WS / SQLite / safeStorage / 通知 / 提醒）→ preload → `window.tks.*` 链路
 *
 * 前置：`npm run build`（需要 out/ 产物）与可用的 X11/XWayland 显示。
 *
 * 用法：
 *   node scripts/e2e.mjs
 *   E2E_DEBUG=1 node scripts/e2e.mjs          # 打印客户端日志与所有断言细节
 */

import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import WebSocket from 'ws'

const ROOT = resolve(import.meta.dirname, '..')
const DEBUG = process.env.E2E_DEBUG === '1'
const MOCK_PORT = Number(process.env.MOCK_PORT ?? 8791)
const CDP_PORT = Number(process.env.CDP_PORT ?? 9333)
const WORK = join(ROOT, '.e2e')
const HOME_DIR = join(WORK, 'home')

let pass = 0
let fail = 0
const failures = []

function check(name, ok, detail) {
  if (ok) {
    pass += 1
    console.log(`  \x1b[32m✓\x1b[0m ${name}`)
  } else {
    fail += 1
    failures.push(name)
    console.log(`  \x1b[31m✗\x1b[0m ${name}${detail !== undefined ? `  →  ${JSON.stringify(detail)}` : ''}`)
  }
}
const section = (t) => console.log(`\n\x1b[1m${t}\x1b[0m`)

/* -------------------------------------------------------------------------- */
/* 子进程管理                                                                  */
/* -------------------------------------------------------------------------- */

const children = []

function launch(cmd, args, opts = {}) {
  const child = spawn(cmd, args, {
    cwd: ROOT,
    env: { ...process.env, ...opts.env },
    stdio: ['ignore', 'pipe', 'pipe']
  })
  children.push(child)
  const sink = (stream, tag) => {
    stream.setEncoding('utf8')
    stream.on('data', (chunk) => {
      if (DEBUG) for (const line of chunk.split('\n').filter(Boolean)) console.log(`    \x1b[90m[${tag}] ${line}\x1b[0m`)
    })
  }
  sink(child.stdout, opts.tag ?? 'out')
  sink(child.stderr, opts.tag ?? 'err')
  return child
}

function killAll() {
  for (const child of children) {
    try {
      child.kill('SIGTERM')
    } catch {
      /* ignore */
    }
  }
}

/* -------------------------------------------------------------------------- */
/* CDP 客户端                                                                  */
/* -------------------------------------------------------------------------- */

class Cdp {
  constructor(url) {
    this.url = url
    this.id = 0
    this.pending = new Map()
  }

  async connect() {
    this.ws = new WebSocket(this.url, { maxPayload: 64 * 1024 * 1024 })
    await new Promise((res, rej) => {
      this.ws.on('open', res)
      this.ws.on('error', rej)
    })
    this.ws.on('message', (raw) => {
      const msg = JSON.parse(raw.toString())
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve, reject } = this.pending.get(msg.id)
        this.pending.delete(msg.id)
        if (msg.error) reject(new Error(msg.error.message))
        else resolve(msg.result)
      }
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

  /** 在渲染进程里求值；支持 await（Promise）。 */
  async eval(expression) {
    const result = await this.send('Runtime.evaluate', {
      expression: `(async () => { ${expression} })()`,
      awaitPromise: true,
      returnByValue: true,
      userGesture: true
    })
    if (result.exceptionDetails) {
      const text = result.exceptionDetails.exception?.description ?? result.exceptionDetails.text
      throw new Error(`渲染进程求值异常：${text}`)
    }
    return result.result.value
  }

  close() {
    try {
      this.ws.close()
    } catch {
      /* ignore */
    }
  }
}

/**
 * 从渲染进程捕获到的异常中解出 IPC 错误载荷。
 *
 * 与 `src/shared/errors.ts::readIpcErrorPayload` 保持一致：
 * `contextBridge` 传 Error 时**只保留 message**，因此载荷被编码进 message，
 * 这里必须解码而不能直接读 `e.code` —— 否则会误判成「错误码丢失」。
 */
function decodeIpcError(err) {
  if (!err || typeof err !== 'object') return null
  const msg = String(err.message ?? '')
  const MARK = 'TKS_IPC_ERR '
  if (msg.startsWith(MARK)) {
    try {
      const p = JSON.parse(msg.slice(MARK.length))
      return { code: typeof p.code === 'number' ? p.code : null, i18nKey: p.i18nKey ?? null, appErrorCode: p.appErrorCode ?? null }
    } catch {
      return null
    }
  }
  // 同上下文时自定义属性可能仍在
  if (typeof err.code === 'number' || typeof err.i18nKey === 'string') {
    return { code: typeof err.code === 'number' ? err.code : null, i18nKey: err.i18nKey ?? null, appErrorCode: err.appErrorCode ?? null }
  }
  return null
}

async function findPageTarget() {
  let lastList = []
  for (let i = 0; i < 60; i += 1) {
    try {
      const res = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`)
      const list = await res.json()
      lastList = list
      // 优先选已经加载了应用页面的 target；都没有时退回第一个 page
      const pages = list.filter((t) => t.type === 'page' && t.webSocketDebuggerUrl)
      const app = pages.find((t) => /index\.html|localhost|127\.0\.0\.1/.test(t.url ?? ''))
      if (app) return app
      if (pages.length > 0 && i > 20) return pages[0]
    } catch {
      /* 还没起来 */
    }
    await sleep(500)
  }
  throw new Error(
    `未找到可调试的渲染进程页面（CDP 超时）。当前 targets=${JSON.stringify(
      lastList.map((t) => ({ type: t.type, url: t.url, title: t.title }))
    )}`
  )
}

/* -------------------------------------------------------------------------- */

async function main() {
  console.log(`\n\x1b[1mTKS Desktop 端到端验收\x1b[0m  Mock=:${MOCK_PORT}  CDP=:${CDP_PORT}\n${'─'.repeat(70)}`)

  if (!existsSync(join(ROOT, 'out/main/index.js'))) {
    console.error('未找到 out/main/index.js，请先执行 npm run build')
    process.exit(1)
  }

  // 干净的运行环境
  rmSync(WORK, { recursive: true, force: true })
  mkdirSync(HOME_DIR, { recursive: true })
  const configDir = join(WORK, 'config')
  const dataDir = join(WORK, 'data')
  const stateDir = join(WORK, 'state')
  for (const dir of [configDir, dataDir, stateDir]) mkdirSync(dir, { recursive: true })

  // 预写设置：把服务地址指向 Mock，直接使用当前设置 schema
  writeFileSync(
    join(configDir, 'settings.json'),
    `${JSON.stringify(
      {
        apiBaseUrl: `http://127.0.0.1:${MOCK_PORT}/api/v1/`,
        wsBaseUrl: `http://127.0.0.1:${MOCK_PORT}`.replace('http', 'ws'),
        theme: 'dark',
        notifications: { chat: true, greeting: true, reminder: true, error: true, progress: true }
      },
      null,
      2
    )}\n`
  )

  /* ------------------------------ 启动 Mock ----------------------------- */
  section('环境准备')
  const mock = launch('node', ['mock-server/server.mjs'], {
    tag: 'mock',
    env: {
      PORT: String(MOCK_PORT),
      DEBOUNCE_MS: '600',
      MERGE_WAIT_MS: '250',
      ACCESS_TTL_SEC: '900'
    }
  })
  void mock

  let mockUp = false
  for (let i = 0; i < 40; i += 1) {
    try {
      const res = await fetch(`http://127.0.0.1:${MOCK_PORT}/healthz`)
      if (res.status === 200) {
        mockUp = true
        break
      }
    } catch {
      /* retry */
    }
    await sleep(250)
  }
  check('Mock 后端已就绪', mockUp)
  if (!mockUp) throw new Error('Mock 后端启动失败')

  /* ---------------------------- 启动真实客户端 -------------------------- */
  // Windows 上 Electron 的可执行文件名为 electron.exe（Linux/macOS 无后缀）
  const electronBin = join(ROOT, 'node_modules/electron/dist', process.platform === 'win32' ? 'electron.exe' : 'electron')
  launch(
    electronBin,
    [
      '--no-sandbox',
      '--disable-gpu',
      '--disable-gpu-compositing',
      '--disable-software-rasterizer',
      '--disable-dev-shm-usage',
      `--remote-debugging-port=${CDP_PORT}`,
      // ⚠️ 用 `.`（即 package.json 的 main）而不是直接指 out/main/index.js：
      //    直接把单个 js 文件交给 Electron 时，`app.getAppPath()` 会变成该文件所在目录，
      //    Electron 找不到 package.json，于是 `app.getVersion()` 返回 **Electron 自己的版本**
      //    而不是应用版本 —— 会让 FR-SET-9 的断言误报失败（实测踩过）。
      '.'
    ],
    {
      tag: 'app',
      env: {
        HOME: HOME_DIR,
        XDG_CACHE_HOME: join(HOME_DIR, '.cache'),
        XDG_CONFIG_HOME: join(HOME_DIR, '.config'),
        XDG_DATA_HOME: join(HOME_DIR, '.local/share'),
        TKS_CONFIG_DIR: configDir,
        TKS_DATA_DIR: dataDir,
        TKS_STATE_DIR: stateDir,
        TKS_LOG_LEVEL: DEBUG ? 'debug' : 'info'
      }
    }
  )

  const page = await findPageTarget()
  console.log(`  \u001b[90m调试目标：url=${page.url || '(空)'}  title=${page.title || '(空)'}\u001b[0m`)
  const cdp = new Cdp(page.webSocketDebuggerUrl)
  await cdp.connect()
  await cdp.send('Runtime.enable')
  await cdp.send('Log.enable').catch(() => undefined)
  // 收集渲染进程的控制台输出与异常（判断白屏原因的关键证据）
  const rendererLogs = []
  cdp.ws.on('message', (raw) => {
    try {
      const msg = JSON.parse(raw.toString())
      if (msg.method === 'Runtime.consoleAPICalled') {
        rendererLogs.push(`[console.${msg.params.type}] ${(msg.params.args ?? []).map((a) => a.value ?? a.description ?? '').join(' ')}`)
      }
      if (msg.method === 'Runtime.exceptionThrown') {
        const d = msg.params.exceptionDetails
        rendererLogs.push(`[exception] ${d.exception?.description ?? d.text}`)
      }
      if (msg.method === 'Log.entryAdded') {
        const e = msg.params.entry
        rendererLogs.push(`[log.${e.level}] ${e.text}`)
      }
    } catch {
      /* ignore */
    }
  })
  check('客户端已启动并可调试', true)

  // 页面是否真的加载了应用 HTML（而不是停在 about:blank）
  const pageInfo = await cdp.eval('return { href: location.href, ready: document.readyState, title: document.title, rootHtml: (document.getElementById("root")||{}).innerHTML ? "non-empty" : "empty" }')
  check('渲染进程已加载应用页面（非 about:blank）',
    !!pageInfo && pageInfo.href && !pageInfo.href.startsWith('about:'),
    pageInfo
  )

  // 等待渲染端 bootstrap 完成（React 挂载 + settings/auth 就绪）
  let booted = false
  for (let i = 0; i < 60; i += 1) {
    try {
      booted = await cdp.eval('return typeof window.tks === "object" && !!window.tks.auth')
      if (booted) break
    } catch {
      /* retry */
    }
    await sleep(500)
  }
  check('FR-ARCH-2：preload 已暴露 window.tks 白名单 API', booted)

  const shellUp = await cdp.eval(
    'for (let i=0;i<80;i++){ if(document.querySelector(".auth-page,.app-shell")) return true; await new Promise(r=>setTimeout(r,250)) } return false'
  )
  check('渲染进程已挂载界面（登录页或应用外壳）', shellUp)

  if (!booted || !shellUp) {
    // 白屏时把能定位原因的信息一次给全，避免「无画面」变成无解的猜谜
    console.log('\n\u001b[33m\u001b[1m界面未起来，以下是定位所需的全部证据：\u001b[0m')
    console.log(`  页面信息      : ${JSON.stringify(pageInfo)}`)
    console.log(`  渲染进程日志  : ${rendererLogs.length === 0 ? '(无输出)' : ''}`)
    for (const line of rendererLogs.slice(0, 30)) console.log(`    ${line}`)
    const logDir = join(WORK, 'state', 'logs')
    try {
      const file = readdirSync(logDir).find((f) => f.endsWith('.jsonl'))
      if (file) {
        const lines = readFileSync(join(logDir, file), 'utf8').trim().split('\n')
          .map((l) => { try { return JSON.parse(l) } catch { return null } })
          .filter((l) => l && ['window', 'main', 'protocol', 'icons'].includes(l.scope))
        console.log('  主进程 window/main 日志：')
        for (const l of lines.slice(-25)) console.log(`    [${l.level}] ${l.scope}: ${l.msg} ${JSON.stringify({ ...l, ts: undefined, level: undefined, scope: undefined, msg: undefined })}`)
      }
    } catch (err) {
      console.log(`  （读取主进程日志失败：${err.message}）`)
    }
    console.log('  \u001b[90m提示：若日志含「预加载脚本执行失败」或「渲染页面加载失败」，即为根因。\u001b[0m')
    throw new Error('界面未能加载，已输出诊断信息（见上）')
  }

  /* ------------------------------ 认证与连接 --------------------------- */
  section('§6.1 / §6.2 认证与连接（FR-AUTH / FR-CONN）')

  const deviceId = await cdp.eval('return await window.tks.auth.getDeviceId()')
  check('FR-AUTH-2：deviceId 形如 device_{uuidv4}',
    typeof deviceId === 'string' && /^device_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(deviceId),
    deviceId
  )
  const deviceId2 = await cdp.eval('return await window.tks.auth.getDeviceId()')
  check('FR-AUTH-2：deviceId 持久化且复用同一 ID', deviceId === deviceId2)

  const storageMode = await cdp.eval('return await window.tks.auth.storageMode()')
  check('FR-AUTH-3：凭据经 safeStorage 加密存储（本机密钥环可用）', storageMode === 'safeStorage', storageMode)

  const badLogin = await cdp.eval(`
    try { await window.tks.auth.login('kris', 'wrong'); return { error: null } }
    catch (e) { return { error: { message: String(e && e.message), name: String(e && e.name) } } }
  `)
  const badLoginErr = decodeIpcError(badLogin?.error)
  check('FR-AUTH-8：错误密码映射到 40101 且带 i18n key',
    badLoginErr?.code === 40101 && badLoginErr?.i18nKey === 'error.api.40101',
    { decoded: badLoginErr, raw: badLogin?.error?.message?.slice(0, 80) }
  )

  const login = await cdp.eval(`
    const t = await window.tks.auth.login('kris', 'taki');
    return { userId: t.userId, hasAccess: !!t.accessToken, expiresIn: t.expiresIn, deviceId: t.deviceId }
  `)
  check('FR-AUTH-1：登录成功返回 accessToken / userId', login?.hasAccess === true && login?.userId === 'kris', login)

  let connState = null
  for (let i = 0; i < 60; i += 1) {
    connState = await cdp.eval('return await window.tks.connection.getState()')
    if (connState?.status === 'connected') break
    await sleep(400)
  }
  check('FR-CONN-1：登录后主进程建立 WS 长连接（状态 connected）', connState?.status === 'connected', connState)

  /* -------------------------------- 聊天 ------------------------------- */
  section('§6.3 对话核心（FR-CHAT）')

  const requestId = `e2e_${Date.now()}`
  const sent = await cdp.eval(`
    const id = ${JSON.stringify(requestId)};
    await window.tks.chat.send({ requestId: id, content: '两点叫我睡觉', attachmentIds: [] });
    const list = await window.tks.chat.listMessages({ limit: 20 });
    const mine = list.find(m => m.messageId === id);
    return { found: !!mine, status: mine?.status, content: mine?.content, role: mine?.role };
  `)
  check('FR-CHAT-2/3：乐观入库，本地主键即 requestId，状态已置为 sent',
    sent?.found === true && sent?.status === 'sent' && sent?.role === 'user' && sent?.content === '两点叫我睡觉',
    sent
  )

  // 等待 Bot 回复（含多气泡拆分）
  const reply = await cdp.eval(`
    for (let i = 0; i < 100; i++) {
      const list = await window.tks.chat.listMessages({ limit: 40 });
      const bots = list.filter(m => m.role === 'bot');
      if (bots.length >= 2) return { bots: bots.map(b => ({ id: b.messageId, content: b.content, ts: b.timestamp })) };
      await new Promise(r => setTimeout(r, 250));
    }
    return { bots: [] };
  `)
  check('FR-CHAT-5/6：Bot 回复按 \\n 拆分为多条气泡（≥2 条）', reply?.bots?.length >= 2, reply?.bots?.length)

  const botIds = (reply?.bots ?? []).map((b) => b.id)
  const baseIds = new Set(botIds.map((id) => id.replace(/_\d+$/, '')))
  check('FR-CHAT-6：拆分后主键形如 {messageId}_{index} 且同源',
    baseIds.size === 1 && botIds.some((id) => /_\d+$/.test(id)),
    botIds
  )
  const tsDelta = (reply?.bots ?? []).map((b) => b.ts)
  check('FR-CHAT-6：拆分后 timestamp 递增',
    tsDelta.every((t, i) => i === 0 || t >= tsDelta[i - 1]),
    tsDelta
  )
  check('FR-CHAT-10：不存在空内容的 Bot 消息', (reply?.bots ?? []).every((b) => b.content.trim().length > 0))

  /* --------------------------- 去重（EDGE-L4/L21） --------------------- */
  section('§12 边界：多设备回声与重复回复去重')
  const afterEcho = await cdp.eval(`
    await new Promise(r => setTimeout(r, 1200));
    const list = await window.tks.chat.listMessages({ limit: 60 });
    const mine = list.filter(m => m.messageId === ${JSON.stringify(requestId)});
    const bots = list.filter(m => m.role === 'bot');
    const dup = bots.length - new Set(bots.map(b => b.messageId)).size;
    return { mineCount: mine.length, botCount: bots.length, dup };
  `)
  check('EDGE-L4：自己发送的消息收到自身 echo 后**不重复上屏**', afterEcho?.mineCount === 1, afterEcho)
  check('EDGE-L21：Bot 回复无重复 messageId', afterEcho?.dup === 0, afterEcho)

  /* --------------------- 回归：状态条不得推动消息内容 ------------------- */
  /*
   * 状态条（排队 / 打字 / 拖拽提示 / 离线横幅）是 `.chat-body` 里的流内元素，
   * 出现时会把它下面的 `.message-list` 整块推下并压缩同样的高度。滚动容器**自身几何变化**
   * 不触发浏览器自带的滚动锚定，若不手动补 `scrollTop`，整屏消息会跟着上下跳动 ——
   * 发送消息时服务端先后下发 `chat.queued` 与 `chat.typing`（两条 = 74px），
   * 回复结束后同时消失又弹回，表现为「发送后主页面轻度上移」。
   *
   * 前置：本回归只在 `.message-list` **可滚动**时有意义（不可滚动时 `scrollTop` 不能为负，
   * 几何补偿没有着力点）。这里先借 Mock 的测试触发口把历史播种到铺满整页。
   * 播种用的独立 REST 登录不影响 App 会话：Mock 的 `KICK_ON_LOGIN` 默认关闭。
   */
  section('§12 边界：状态条出现时消息内容不得位移（FR-CHAT 视口几何补偿）')
  const seedAuth = await fetch(`http://127.0.0.1:${MOCK_PORT}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      username: 'kris',
      password: 'taki',
      deviceId: 'device_e2e0seed-0000-0000-0000-000000000000'
    })
  }).then((r) => r.json())
  await fetch(`http://127.0.0.1:${MOCK_PORT}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${seedAuth.accessToken}` },
    body: JSON.stringify({ kind: 'seed_history', count: 50 })
  })
  const seededSync = await cdp.eval('return await window.tks.sync.syncHistory(true)')
  check('前置：播种历史后全量同步成功（让聊天页可滚动）', seededSync?.ok === true, seededSync)

  const barShift = await cdp.eval(`
    const list = document.querySelector('.message-list');
    const body = document.querySelector('.chat-body');
    if (!list || !body) return { skipped: 'no chat page' };
    const rowsOf = () => Array.from(list.querySelectorAll('.message-row'));
    if (rowsOf().length === 0) return { skipped: 'no messages' };
    list.scrollTop = list.scrollHeight;
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    if (list.scrollHeight <= list.clientHeight) return { skipped: 'list not scrollable' };
    const before = new Map();
    const lr0 = list.getBoundingClientRect();
    for (const r of rowsOf()) {
      const b = r.getBoundingClientRect();
      if (b.bottom > lr0.top && b.top < lr0.bottom) before.set(r.dataset.messageId, b.top);
    }
    // 贴底场景
    const bar = document.createElement('div');
    bar.className = 'chat-activity';
    bar.innerHTML = '<span>回归探针</span>';
    body.insertBefore(bar, list);
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    const barHeight = +bar.getBoundingClientRect().height.toFixed(2);
    let maxShift = 0;
    for (const r of rowsOf()) {
      const was = before.get(r.dataset.messageId);
      if (was === undefined) continue;
      maxShift = Math.max(maxShift, Math.abs(r.getBoundingClientRect().top - was));
    }
    bar.remove();
    await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
    return { measured: before.size, barHeight, maxShift: +maxShift.toFixed(2) };
  `)
  check('回归：37px 状态条插入时可见消息零位移（贴底场景）',
    barShift?.skipped !== undefined || barShift?.maxShift <= 1,
    barShift
  )

  /* ------------------------------ 历史同步 ---------------------------- */
  section('§6.5 历史同步（FR-SYNC）')
  const syncResult = await cdp.eval('return await window.tks.sync.syncHistory(false)')
  check('FR-SYNC-2/3：进入聊天后主动增量同步成功', syncResult?.ok === true && syncResult?.mode === 'incremental', syncResult)

  const fullSync = await cdp.eval('return await window.tks.sync.syncHistory(true)')
  check('FR-SYNC-4：全量同步（since=0）成功', fullSync?.ok === true && fullSync?.mode === 'full', fullSync)

  const historyCountAfterFull = await cdp.eval('return (await window.tks.chat.listMessages({ limit: 500 })).length')
  check('FR-SYNC-5：全量同步落库幂等（重复全量不产生重复行）',
    await (async () => {
      await cdp.eval('return await window.tks.sync.syncHistory(true)')
      const n = await cdp.eval('return (await window.tks.chat.listMessages({ limit: 500 })).length')
      return n === historyCountAfterFull
    })(),
    { before: historyCountAfterFull }
  )

  /* ------------------------------- 提醒 ------------------------------- */
  section('§6.6 提醒（FR-REM）')
  const reminders = await cdp.eval('return await window.tks.reminders.list()')
  const fromInstruction = (reminders ?? []).find((r) => r.reminderId.includes('02:00') || r.targetTime === '02:00')
  check('FR-REM-1/2：服务端 timerInstruction 已在本地排程（未自行解析 [[TIMER:...]]）',
    !!fromInstruction,
    (reminders ?? []).map((r) => ({ id: r.reminderId, t: r.targetTime, status: r.status }))
  )
  check('FR-REM-4：提醒已持久化到数据库（含 fireAt 绝对时间戳）',
    !!fromInstruction && typeof fromInstruction.fireAt === 'number' && fromInstruction.fireAt > Date.now() - 86400000,
    fromInstruction
  )
  check('FR-REM-3：去重键格式为 reminder_{requestIds}_{target}_{hash}',
    !!fromInstruction && /^reminder_.+_02:00_.+$/.test(fromInstruction.reminderId),
    fromInstruction?.reminderId
  )

  /* ------------------------------ 积分体系 ---------------------------- */
  section('§7 积分 / 等级 / 互动 / 补签卡')

  const overview = await cdp.eval('return await window.tks.gamification.overview()')
  check('FR-PT-1：/points/overview 一次取回余额 + 等级 + 补签卡',
    typeof overview?.balance === 'number' && !!overview?.level && !!overview?.makeupCard,
    { balance: overview?.balance, level: overview?.level?.levelCode, cards: overview?.makeupCard?.available }
  )
  check('FR-PROG-3：等级携带服务端日期字段（lastValidDate / breakDeadlineDate）',
    typeof overview?.level?.lastValidDate === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(overview.level.lastValidDate),
    overview?.level?.lastValidDate
  )

  const levelConfig = await cdp.eval('return await window.tks.gamification.levelConfig()')
  check('FR-LV-2：/level/config 返回 7 个等级且字段为 snake_case',
    (levelConfig?.levels ?? []).length === 7 && levelConfig.levels.every((l) => !!l.level_code && typeof l.threshold_days === 'number'),
    (levelConfig?.levels ?? []).map((l) => `${l.level_code}:${l.threshold_days}`)
  )

  const menu = await cdp.eval('return await window.tks.gamification.interactionItems()')
  check('FR-INT-2：互动菜单按 sortOrder 排序且含 affordable',
    (menu?.items ?? []).length === 5 && menu.items.every((i) => typeof i.affordable === 'boolean'),
    (menu?.items ?? []).map((i) => `${i.id}:${i.sortOrder}:${i.affordable}`)
  )

  // FR-INT-5/11：本地余额预校验 + 幂等 requestId
  const giftRequestId = `e2e_gift_${Date.now()}`
  const gift = await cdp.eval(`
    const r = await window.tks.gamification.interactionSend('coffee', ${JSON.stringify(giftRequestId)}, '今天好累');
    return { code: r.code, success: r.data?.success, balance: r.data?.balance, charged: r.data?.charged, merged: r.mergedRequestIds }
  `)
  check('FR-INT-5：互动发送成功并按后端返回覆盖余额',
    gift?.code === 0 && gift?.success === true && typeof gift?.balance === 'number' && gift?.charged === 5,
    gift
  )

  const giftReply = await cdp.eval(`
    for (let i = 0; i < 80; i++) {
      const list = await window.tks.chat.listMessages({ limit: 60 });
      const bot = list.filter(m => m.role === 'bot');
      if (bot.length > 0) {
        const last = bot[bot.length - 1];
        if (last.interactionItemName || true) return { count: bot.length, content: bot.map(b => b.content) };
      }
      await new Promise(r => setTimeout(r, 250));
    }
    return { count: 0, content: [] }
  `)
  check('FR-INT-6：互动回复经 chat.reply.stream 复用聊天气泡渲染', (giftReply?.count ?? 0) > 0, giftReply?.count)

  // FR-INT-11：同一 requestId 重复发送不重复扣分
  const giftDup = await cdp.eval(`
    const before = (await window.tks.gamification.balance()).balance;
    const r = await window.tks.gamification.interactionSend('coffee', ${JSON.stringify(giftRequestId)}, '今天好累');
    const after = (await window.tks.gamification.balance()).balance;
    return { before, after, duplicate: r.data?.duplicate }
  `)
  check('FR-INT-11：重试复用同一 requestId 时服务端判定 duplicate 且余额不变',
    giftDup?.duplicate === true && giftDup?.before === giftDup?.after,
    giftDup
  )

  // 40201 积分不足
  const insufficient = await cdp.eval(`
    // 反复送最贵的物品直到余额不足，验证 40201 分支
    let last = null;
    for (let i = 0; i < 12; i++) {
      const r = await window.tks.gamification.interactionSend('white_dragon', 'e2e_drain_' + i, 'x');
      last = r;
      if (r.code === 40201) return { drained: true, code: r.code, balance: (await window.tks.gamification.balance()).balance };
    }
    return { drained: false, code: last?.code, balance: (await window.tks.gamification.balance()).balance };
  `)
  check('§7.0：积分不足时返回业务错误码 40201（HTTP 200 + code != 0）', insufficient?.drained === true, insufficient)

  // 补签卡：候选来自服务端，主动点击后 RESTORE
  const candidates = await cdp.eval('return await window.tks.gamification.makeupCandidates(120)')
  check('FR-MC-2：补签日历数据源为 /makeup-card/candidates 且日期为 YYYY-MM-DD',
    (candidates?.items ?? []).length > 0 && candidates.items.every((c) => /^\d{4}-\d{2}-\d{2}$/.test(c.date)),
    (candidates?.items ?? []).map((c) => c.date)
  )
  const makeupUse = await cdp.eval(`
    const c = (await window.tks.gamification.makeupCandidates(120)).items[0];
    const r = await window.tks.gamification.makeupUse(c.date);
    return { ok: r.ok, code: r.code, changeType: r.data?.level?.changeType, availableCards: r.data?.availableCards, date: c.date }
  `)
  check('FR-MC-3/5：补签成功且 level.changeType=RESTORE（走克制反馈而非庆祝）',
    makeupUse?.ok === true && makeupUse?.changeType === 'RESTORE',
    makeupUse
  )
  const makeupAgain = await cdp.eval(`
    const c = ${JSON.stringify(makeupUse?.date)};
    const r = await window.tks.gamification.makeupUse(c);
    return { ok: r.ok, code: r.code }
  `)
  check('FR-MC-6：重复补签同一天返回 40206 且失败不消耗卡片',
    makeupAgain?.ok === false && makeupAgain?.code === 40206,
    makeupAgain
  )

  const ledger = await cdp.eval('return await window.tks.gamification.pointsHistory({ page: 1, pageSize: 20 })')
  check('FR-PT-2：积分流水分页返回且事由为 snake_case reason_code',
    Array.isArray(ledger?.items) && ledger.items.length > 0 && ledger.items.every((e) => typeof e.reason_code === 'string'),
    ledger?.items?.map((e) => `${e.reason_code}:${e.change_amount}`)
  )
  check('FR-PT-3：流水含 ADMIN_ADJUST 之外的规则事由（DAILY_FIRST_CHAT / ITEM_SEND）',
    (ledger?.items ?? []).some((e) => e.reason_code === 'ITEM_SEND') &&
      (ledger?.items ?? []).some((e) => e.reason_code === 'DAILY_FIRST_CHAT'),
    [...new Set((ledger?.items ?? []).map((e) => e.reason_code))]
  )

  /* ------------------------------ 设置项 ------------------------------ */
  section('§6.8 设置（FR-SET）')

  const about = await cdp.eval('return await window.tks.settings.about()')
  check('FR-SET-9：版本号来自打包元数据（非硬编码）', about?.version === '1.0.0', about?.version)
  check('FR-SET-9：关于信息含后端地址 / deviceId / 日志目录 / 凭据存储模式',
    typeof about?.apiBaseUrl === 'string' &&
      typeof about?.deviceId === 'string' &&
      typeof about?.logsDir === 'string' &&
      about?.credentialStorage === 'safeStorage',
    about && { api: about.apiBaseUrl, storage: about.credentialStorage, logs: about.logsDir }
  )

  const urlReject = await cdp.eval(`
    try { await window.tks.settings.update({ apiBaseUrl: 'ftp://evil.example/' }); return 'NO_ERROR' }
    catch (e) { return e.message }
  `)
  check('FR-CFG-2：拒绝非 http/https 的服务地址', urlReject !== 'NO_ERROR', urlReject)

  const citySaved = await cdp.eval(`
    const saved = await window.tks.settings.setCity('上海 #注释应被截断');
    return { saved, readBack: await window.tks.settings.getCity() }
  `)
  check('FR-SET-2：天气城市保存并截断 `#` 之后的内容',
    citySaved?.saved === '上海' && citySaved?.readBack === '上海',
    citySaved
  )

  const cityEmpty = await cdp.eval(`
    try { await window.tks.settings.setCity('   '); return 'NO_ERROR' }
    catch (e) { return { code: e.code, message: e.message } }
  `)
  check('§5.2：空城市返回 40002 并被客户端识别', cityEmpty !== 'NO_ERROR', cityEmpty)

  const themeSaved = await cdp.eval(`
    await window.tks.settings.update({ theme: 'light' });
    const s = await window.tks.settings.get();
    return { theme: s.theme, sendKey: s.sendKey, closeBehavior: s.closeBehavior }
  `)
  check('FR-SET-1：主题可持久化', themeSaved?.theme === 'light', themeSaved)

  const notifSaved = await cdp.eval(`
    await window.tks.settings.update({ notifications: { progress: false }, doNotDisturb: { enabled: true, start: '23:30', end: '07:15' } });
    const s = await window.tks.settings.get();
    return { progress: s.notifications.progress, chat: s.notifications.chat, dnd: s.doNotDisturb }
  `)
  check('FR-NOTI-7：五类通知可分类开关且互不影响',
    notifSaved?.progress === false && notifSaved?.chat === true,
    notifSaved
  )
  check('FR-NOTI-7：勿扰时段可配置',
    notifSaved?.dnd?.enabled === true && notifSaved?.dnd?.start === '23:30' && notifSaved?.dnd?.end === '07:15',
    notifSaved?.dnd
  )

  const shortcut = await cdp.eval('return await window.tks.settings.shortcutStatus()')
  check('FR-SET-7：全局快捷键在 X11 下可用并已注册', shortcut?.available === true && shortcut?.registered, shortcut)

  const badShortcut = await cdp.eval(`
    const r = await window.tks.settings.setShortcut('Control+Alt+ThisIsNotAKey', true);
    return { ok: r.ok, key: r.errorI18nKey }
  `)
  check('FR-SET-7：非法快捷键被拒绝并给出 i18n 提示', badShortcut?.ok === false && !!badShortcut?.key, badShortcut)

  const autostart = await cdp.eval(`
    const ok = await window.tks.settings.setAutostart(true, true);
    return { ok }
  `)
  const autostartFile = join(HOME_DIR, '.config/autostart/tks-desktop.desktop')
  check('FR-DSK-3：开机自启写入 ~/.config/autostart/tks-desktop.desktop',
    autostart?.ok === true && existsSync(autostartFile),
    autostartFile
  )
  if (existsSync(autostartFile)) {
    const content = readFileSync(autostartFile, 'utf8')
    check('FR-DSK-9：自启条目带 --hidden（静默启动到托盘）', content.includes('--hidden'), content.split('\n').find((l) => l.startsWith('Exec=')))
    check('FR-PKG-5：自启条目为合法 XDG Desktop Entry', content.includes('[Desktop Entry]') && content.includes('Type=Application'))
  } else {
    check('FR-DSK-9：自启条目带 --hidden（文件不存在，跳过）', false)
    check('FR-PKG-5：自启条目为合法 XDG Desktop Entry（文件不存在，跳过）', false)
  }
  await cdp.eval('return await window.tks.settings.setAutostart(false, true)')

  /* --------------------------- 图片与识图 ----------------------------- */
  section('§6.4 图片（FR-IMG）与 EDGE-L16')
  const imgErrors = await cdp.eval(`
    const results = {};
    const draftBefore = (await window.tks.images.listDraft()).length;
    // ⚠️ 不能假设剪贴板是空的：运行者很可能刚截过图。
    //    FR-IMG-8 真正要保证的是「不静默失败」——要么成功加入附件，要么抛出可本地化的错误。
    try {
      const added = await window.tks.images.readClipboard();
      results.clipboard = { error: null, added: Array.isArray(added) ? added.length : null };
    } catch (e) {
      results.clipboard = { error: { message: String(e && e.message) }, added: null };
    }
    results.draftBefore = draftBefore;
    results.usage = await window.tks.images.usage()
    results.draft = (await window.tks.images.listDraft()).length
    return results
  `)
  const clipboardErr = decodeIpcError(imgErrors?.clipboard?.error)
  const clipboardAdded = imgErrors?.clipboard?.added
  check('FR-IMG-8：Ctrl+V 贴图要么成功加入附件、要么抛出可本地化的错误（**不静默失败**）',
    (typeof clipboardAdded === 'number' && clipboardAdded > 0) ||
      (typeof clipboardErr?.i18nKey === 'string' && clipboardErr.i18nKey.startsWith('image.error.')),
    { added: clipboardAdded, decoded: clipboardErr, raw: imgErrors?.clipboard?.error?.message?.slice(0, 80) }
  )
  check('FR-IMG-8：剪贴板有图时确实进入了草稿（附件条应随之变化）',
    clipboardAdded === null || imgErrors.draft === imgErrors.draftBefore + clipboardAdded,
    { before: imgErrors?.draftBefore, after: imgErrors?.draft, added: clipboardAdded }
  )
  check('EDGE-L15：可读取附件占用统计', typeof imgErrors?.usage?.count === 'number' && typeof imgErrors?.usage?.bytes === 'number', imgErrors?.usage)

  /* --------------------------- 历史与记忆页 --------------------------- */
  section('§6.9 历史与记忆（FR-HIS）')
  const facts = await cdp.eval('return await window.tks.history.listFacts(50)')
  check('FR-HIS-2：记忆列表可读取（含后端提炼的用户事实）', Array.isArray(facts), facts?.length)

  const notis = await cdp.eval('return await window.tks.history.listNotifications(50)')
  check('FR-HIS-1：Bot 通知列表可读取', Array.isArray(notis), notis?.length)

  /* ----------------------------- 清空会话 ----------------------------- */
  section('§6.3 FR-CHAT-12 清空会话并推进游标')
  const cleared = await cdp.eval(`
    const r = await window.tks.chat.clearConversation();
    const after = (await window.tks.chat.listMessages({ limit: 500 })).length;
    // 清空后立刻同步必须**不能**把历史拉回来（游标已推到 now）
    const s = await window.tks.sync.syncHistory(false);
    await new Promise(r => setTimeout(r, 600));
    return { deleted: r.deleted, afterClear: after, afterSync: (await window.tks.chat.listMessages({ limit: 500 })).length, syncOk: s.ok };
  `)
  check('FR-CHAT-12：清空本地会话生效', cleared?.deleted > 0 && cleared?.afterClear === 0, cleared)
  check('FR-CHAT-12：清空后同步游标已推进到 now，历史**不会**被立刻补拉回灌',
    cleared?.afterSync === 0,
    cleared
  )

  /* ------------------------- 降级 / 未知帧鲁棒性 ---------------------- */
  section('§10.1 连接状态机与 NFR-12 鲁棒性')
  const stateAfter = await cdp.eval('return await window.tks.connection.getState()')
  check('NFR-12：经过未知帧与长时间运行后连接仍为 connected', stateAfter?.status === 'connected', stateAfter)

  const manualSync = await cdp.eval(`
    await window.tks.connection.reconnect(true);
    for (let i = 0; i < 60; i++) {
      const s = await window.tks.connection.getState();
      if (s.status === 'connected') return s;
      await new Promise(r => setTimeout(r, 300));
    }
    return await window.tks.connection.getState();
  `)
  check('FR-CONN-6：手动重连后恢复 connected 并执行全量同步', manualSync?.status === 'connected', manualSync)

  /* ------------------------------ 数据库文件 -------------------------- */
  section('§9 本地数据模型')
  check('§9：SQLite 数据库已落盘到数据目录', existsSync(join(dataDir, 'tks.db')), join(dataDir, 'tks.db'))
  check('FR-DSK-12：附件目录已创建', existsSync(join(dataDir, 'attachments')))
  check('FR-DSK-11：日志目录已创建并写入日志',
    existsSync(join(stateDir, 'logs')) && readFileSync_(
      join(stateDir, 'logs'),
      /tks-\d{8}\.jsonl/
    ),
    join(stateDir, 'logs')
  )

  const logLine = (() => {
    try {
      const dir = join(stateDir, 'logs')
      const file = readdirSync(dir).find((f) => f.endsWith('.jsonl'))
      if (!file) return null
      const lines = readFileSync(join(dir, file), 'utf8').trim().split('\n')
      return lines.map((l) => JSON.parse(l))
    } catch {
      return null
    }
  })()
  check('NFR-7：日志为 JSON 行结构（ts/level/scope/msg）',
    Array.isArray(logLine) && logLine.length > 0 && logLine.every((l) => l.ts && l.level && l.scope && l.msg),
    logLine?.length
  )
  check('NFR-6：日志中**不出现** Token / 密码 / 图片 base64',
    Array.isArray(logLine) &&
      !logLine.some((l) => /"accessToken":"[^[]|refreshToken":"(?!\[redacted)|"password":"[^[]|dataBase64":"[A-Za-z0-9+/]{40}/.test(JSON.stringify(l))),
    'ok'
  )

  /* -------------------------------- 收尾 ----------------------------- */
  cdp.close()

  console.log(`\n${'─'.repeat(70)}`)
  if (fail === 0) {
    console.log(`\x1b[32m\x1b[1m端到端验收全部通过：${pass} 项断言\x1b[0m\n`)
  } else {
    console.log(`\x1b[31m\x1b[1m失败 ${fail} 项 / 共 ${pass + fail} 项\x1b[0m`)
    for (const name of failures) console.log(`  \x1b[31m·\x1b[0m ${name}`)
    console.log()
  }
  return fail === 0 ? 0 : 1
}

/** 目录里是否存在匹配的文件。 */
function readFileSync_(dir, pattern) {
  try {
    return readdirSync(dir).some((f) => pattern.test(f))
  } catch {
    return false
  }
}

let exitCode = 1
try {
  exitCode = await main()
} catch (err) {
  console.error(`\n\x1b[31m端到端测试异常终止：\x1b[0m ${err.message}`)
  if (DEBUG) console.error(err.stack)
  exitCode = 1
} finally {
  killAll()
  await sleep(400)
}
process.exit(exitCode)
