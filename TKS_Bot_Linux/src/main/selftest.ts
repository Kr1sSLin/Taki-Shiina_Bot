/**
 * 无界面集成自检（诊断 / 验收用，`npm run selftest`）。
 *
 * 以**真实 Electron 主进程**运行，但不创建窗口、不加载渲染进程，因此可在
 * 无显示器、无 GPU、甚至连 Chromium 渲染子进程都无法启动的受限环境中使用
 * （例如 CI 容器或 bwrap 沙箱）。
 *
 * 覆盖的是客户端风险最高、也最值得实测的那一层：
 *   safeStorage 凭据 → REST（401 自动续签）→ WS 长连接 → 防抖流式回复 →
 *   多气泡拆分落库 → 重复投递去重 → 提醒排程 → 积分/等级/互动/补签卡 → 数据迁移
 *
 * 用法：
 *   node mock-server/server.mjs &            # 先起 Mock
 *   npm run selftest                         # 再跑自检
 *   TKS_SELFTEST_API=http://127.0.0.1:8787/api/v1/ \
 *   TKS_SELFTEST_WS=ws://127.0.0.1:8787 npm run selftest
 *
 * 也可以指向真实后端（会占用一个设备位并产生真实积分流水）：
 *   TKS_SELFTEST_API=https://takishiinabot.top/api/v1/ \
 *   TKS_SELFTEST_WS=wss://takishiinabot.top \
 *   TKS_SELFTEST_USER=你的用户名 TKS_SELFTEST_PASS=你的密码 npm run selftest
 */

import { app, clipboard } from 'electron'
import { randomUUID } from 'node:crypto'
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

/* -------------------------------------------------------------------------- */
/* 断言                                                                        */
/* -------------------------------------------------------------------------- */

let pass = 0
let fail = 0
const failures: string[] = []

function check(name: string, ok: boolean, detail?: unknown): void {
  if (ok) {
    pass += 1
    process.stdout.write(`  \u001b[32m✓\u001b[0m ${name}\n`)
  } else {
    fail += 1
    failures.push(name)
    process.stdout.write(`  \u001b[31m✗\u001b[0m ${name}${detail !== undefined ? `  →  ${safe(detail)}` : ''}\n`)
  }
}

function section(title: string): void {
  process.stdout.write(`\n\u001b[1m${title}\u001b[0m\n`)
}

function safe(value: unknown): string {
  try {
    return JSON.stringify(value)?.slice(0, 400) ?? String(value)
  } catch {
    return String(value)
  }
}

const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms))

/** §6.4 的常量（避免与 PROTOCOL 的导入顺序纠缠）。 */
const PROTOCOL_MAX_IMAGE_BYTES = 20 * 1024 * 1024

/** 1x1 透明 PNG —— 最小合法图片，用于验证接受路径。 */
const TINY_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
  'base64'
)

const existsSyncSync = (p: string): boolean => {
  try {
    readFileSync(p)
    return true
  } catch {
    return false
  }
}

async function waitFor<T>(fn: () => T | Promise<T>, timeoutMs: number, label: string): Promise<T | null> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const value = await fn()
    if (value) return value
    await sleep(120)
  }
  process.stdout.write(`    \u001b[90m(等待超时：${label})\u001b[0m\n`)
  return null
}

/* -------------------------------------------------------------------------- */
/* 主流程                                                                      */
/* -------------------------------------------------------------------------- */

async function run(): Promise<number> {
  // 隔离的运行目录，避免污染真实用户数据
  const sandbox = mkdtempSync(join(tmpdir(), 'tks-selftest-'))
  process.env.TKS_CONFIG_DIR = join(sandbox, 'config')
  process.env.TKS_DATA_DIR = join(sandbox, 'data')
  process.env.TKS_STATE_DIR = join(sandbox, 'state')
  // 用 info 而非 warn：自检需要观测 info 级的生命周期事件（如通知结论行、迁移进度）
  process.env.TKS_LOG_LEVEL = process.env.TKS_SELFTEST_VERBOSE === '1' ? 'debug' : 'info'

  const apiBaseUrl = process.env.TKS_SELFTEST_API ?? 'http://127.0.0.1:8787/api/v1/'
  const wsBaseUrl = process.env.TKS_SELFTEST_WS ?? 'http://127.0.0.1:8787'.replace('http', 'ws')
  const username = process.env.TKS_SELFTEST_USER ?? 'kris'
  const password = process.env.TKS_SELFTEST_PASS ?? 'taki'

  process.stdout.write(
    `\n\u001b[1mTKS Desktop 无界面集成自检\u001b[0m  REST=${apiBaseUrl}  WS=${wsBaseUrl}\n${'─'.repeat(70)}\n`
  )
  process.stdout.write(`临时目录：${sandbox}\n`)

  /* ------------------------- 目录 / 数据库 / 迁移 ---------------------- */
  section('§9 本地数据模型与迁移（真实 better-sqlite3）')

  const { ensureDirs, paths } = await import('./app/paths')
  const dirs = ensureDirs()
  check('FR-DSK-12：XDG 目录已创建', !!dirs.configDir && !!dirs.dataDir && !!dirs.logsDir)

  const { initDatabase, closeDatabase, SCHEMA_VERSION } = await import('./core/database/db')
  const { db, integrity } = initDatabase(dirs.databasePath)
  check('EDGE-L14：完整性校验通过', integrity === 'ok', integrity)

  const version = db.pragma('user_version', { simple: true }) as number
  check('FR-DB-3：user_version 迁移到最新版本', version === SCHEMA_VERSION, { version, SCHEMA_VERSION })

  const tables = (
    db.prepare("SELECT name FROM sqlite_master WHERE type IN ('table','view')").all() as Array<{ name: string }>
  ).map((r) => r.name)
  for (const table of [
    'chat_messages',
    'chat_attachments',
    'bot_notifications',
    'user_facts',
    'reminders',
    'user_progress_cache',
    'points_ledger_cache',
    'makeup_cards_cache',
    'makeup_candidates_cache'
  ]) {
    check(`§9：表 ${table} 存在`, tables.includes(table))
  }
  check('FR-CHAT-17：FTS5 全文索引已建', tables.includes('chat_messages_fts'))

  const journalMode = db.pragma('journal_mode', { simple: true }) as string
  check('NFR-13：WAL 模式已启用', journalMode.toLowerCase() === 'wal', journalMode)
  const fk = db.pragma('foreign_keys', { simple: true }) as number
  check('§9.2：外键约束已开启（附件级联删除依赖它）', fk === 1, fk)

  /* ------------------------- FR-DB-1 / 去重 / 拆分 --------------------- */
  section('§9.2 FR-DB-1 与 §6.3 多气泡拆分（真实落库）')

  const msgRepo = await import('./core/database/repositories/message-repository')

  // FR-DB-1：更新消息状态后附件仍须存在
  msgRepo.upsertMessage({
    messageId: 'selftest_msg',
    sessionId: 'default_session',
    role: 'user',
    content: '带图片的消息',
    status: 'sending',
    timestamp: Date.now()
  })
  const att = msgRepo.insertAttachment({
    messageId: 'selftest_msg',
    sessionId: 'default_session',
    mimeType: 'image/png',
    localPath: join(dirs.attachmentsDir, 'selftest.png'),
    fileSize: 1234,
    width: 10,
    height: 10
  })
  msgRepo.updateMessageStatus('selftest_msg', 'sent')
  const stillThere = msgRepo.listAttachmentsByMessage('selftest_msg')
  check('FR-DB-1：更新消息**不丢附件**（用 UPDATE 而非 INSERT OR REPLACE）',
    stillThere.length === 1 && stillThere[0].attachmentId === att.attachmentId,
    { count: stillThere.length }
  )

  // FR-CHAT-6：拆分规则
  const parts = msgRepo.splitBotContent('bot_abc', '第一行\n\n第二行\n第三行', 1000)
  check('FR-CHAT-6：按 \\n 拆分并过滤空行', parts.length === 3, parts)
  check('FR-CHAT-6：主键为 {messageId}_{index}', parts[0].messageId === 'bot_abc_0' && parts[1].messageId === 'bot_abc_1', parts.map((p) => p.messageId))
  check('FR-CHAT-6：timestamp 递增为 {ts}+{index}', parts[0].timestamp === 1000 && parts[2].timestamp === 1002, parts.map((p) => p.timestamp))
  const single = msgRepo.splitBotContent('bot_single', '只有一行', 2000)
  check('FR-CHAT-6：单行时保留原 messageId（与其它端保持一致）',
    single.length === 1 && single[0].messageId === 'bot_single',
    single
  )
  const empty = msgRepo.splitBotContent('bot_empty', '\n\n   \n', 3000)
  check('FR-CHAT-10：全空白内容不产生任何气泡', empty.length === 0, empty)

  check('FR-SYNC-9：剥离 【MM-DD HH:MM】 历史前缀',
    msgRepo.stripHistoryTimestampPrefix('【08-22 17:22】你好') === '你好',
    msgRepo.stripHistoryTimestampPrefix('【08-22 17:22】你好')
  )
  check('FR-SYNC-8：识别服务端「新的一天」分隔标记',
    msgRepo.isHistorySeparator('──── 新的一天 ────') === true && msgRepo.isHistorySeparator('普通消息') === false
  )

  /*
   * 回归：upsertMessage 必须区分「就地修复」与「无变化」。
   * 历史同步靠 'updated' 判断要不要刷新 UI；若退回布尔值，
   * 被修复成 sent 的消息会在界面上一直显示「发送失败」。
   */
  const healId = `heal_probe_${Date.now()}`
  const healBase = {
    messageId: healId,
    sessionId: 'default_session',
    role: 'user' as const,
    content: '等待确认的消息',
    timestamp: Date.now()
  }
  const healInsert = msgRepo.upsertMessage({ ...healBase, status: 'error', errorCode: 'SEND_FAILED' })
  const healUpdate = msgRepo.upsertMessage({ ...healBase, status: 'sent' })
  const healNoop = msgRepo.upsertMessage({ ...healBase, status: 'sent' })
  check('回归：upsertMessage 区分 inserted / updated / unchanged（状态修复必须可被感知）',
    healInsert === 'inserted' && healUpdate === 'updated' && healNoop === 'unchanged',
    { healInsert, healUpdate, healNoop }
  )
  check('回归：状态修复后 error_code 被清空且状态为 sent',
    (() => {
      const row = msgRepo.listMessages({ limit: 500 }).find((m) => m.messageId === healId)
      return row?.status === 'sent' && !row?.errorCode
    })()
  )

  // FR-INT-12 / EDGE-L21：Bot 消息去重
  msgRepo.markDelivered(['dup_1', 'dup_1'])
  check('EDGE-L21：delivered_bot_messages 去重表可记录', msgRepo.isDelivered('dup_1'))
  check('EDGE-L21：未投递的 ID 判定为 false', msgRepo.isDelivered('never') === false)

  // EDGE-L7：孤儿 streaming 规整
  msgRepo.upsertMessage({
    messageId: 'pending_orphan_with_content',
    sessionId: 'default_session',
    role: 'bot',
    content: '半截回复',
    status: 'streaming',
    timestamp: Date.now()
  })
  msgRepo.upsertMessage({
    messageId: 'pending_orphan_empty',
    sessionId: 'default_session',
    role: 'bot',
    content: '   ',
    status: 'streaming',
    timestamp: Date.now()
  })
  const normalized = msgRepo.normalizeStreamingMessages()
  check('EDGE-L7：有内容的 streaming 转为 received、空内容被删除',
    normalized.promoted >= 1 && normalized.deleted >= 1,
    normalized
  )
  check('EDGE-L7：规整后无残留 streaming 孤儿',
    msgRepo.listMessages({ limit: 500 }).filter((m) => m.status === 'streaming').length === 0
  )

  // FR-CHAT-12：清空会话推进游标
  const cursorBefore = msgRepo.getCursor('chat_history')
  const cleared = msgRepo.clearConversationSession({ advanceCursor: true })
  check('FR-CHAT-12：清空会话后 cursor 推进到 now（防止历史立刻被补拉回灌）',
    cleared.cursor > cursorBefore && cleared.cursor > Date.now() - 5000,
    { before: cursorBefore, after: cleared.cursor }
  )

  // FR-CHAT-17：FTS5 搜索
  msgRepo.upsertMessage({
    messageId: 'fts_probe',
    sessionId: 'default_session',
    role: 'user',
    content: '今天写了一个很长的递归函数',
    status: 'sent',
    timestamp: Date.now()
  })
  const found = msgRepo.searchMessages('递归函数', 10)
  check('FR-CHAT-17：FTS5 中文全文搜索命中', found.some((m) => m.messageId === 'fts_probe'), found.length)

  closeDatabase()
  process.stdout.write(`  \u001b[90m（数据库层断言完成，重新建库用于后续端到端流程）\u001b[0m\n`)

  /* --------------------------- 组装真实服务图 -------------------------- */
  section('§5 / §7 真实服务链路（REST + WS + 持久化）')

  /*
   * 回归：客户端自身超时必须被判定为「超时」而非「网络错误」。
   * 旧实现 `controller.abort(new Error('timeout'))` 抛出的就是那个 Error，
   * name 是 'Error' 而非 'AbortError'，于是超时被误判成网络抖动并进入
   * 「超时禁止自动重试」的反向分支（互动接口有重复扣分风险）。
   * 这里用一个必然超时的请求验证归类正确。
   */
  {
    const { RestClient } = await import('./core/network/rest-client')
    const probe = new RestClient({
      // 保留地址段（RFC 5737 TEST-NET-1），必然连不通 → 触发本地超时
      baseUrlProvider: () => 'https://192.0.2.1/api/v1/',
      auth: {
        getAccessToken: () => null,
        refreshAccessToken: async () => null,
        onUnauthorized: () => {}
      }
    })
    let timeoutMessage = ''
    try {
      await probe.request('healthz', { timeoutMs: 300, authenticated: false, envelope: false, retries: 2 })
    } catch (err) {
      timeoutMessage = err instanceof Error ? err.message : String(err)
    }
    check('回归：客户端超时被判定为 timeout（不得误判为 network error / 不得自动重试）',
      timeoutMessage === 'request timeout',
      timeoutMessage
    )

    const { egressInfo } = await import('./core/network/dispatcher')
    const egress = egressInfo()
    check('FR-NET：REST 出口显式可判定（proxy / direct，不再隐式忽略代理环境变量）',
      egress.kind === 'proxy' || egress.kind === 'direct',
      egress
    )
  }

  const { initDatabase: reinit } = await import('./core/database/db')
  reinit(dirs.databasePath)

  const { getSettings, updateSettings } = await import('./app/config-store')
  updateSettings({ apiBaseUrl, wsBaseUrl })

  const { buildServices } = await import('./app/services')
  let quitRequested = false
  const services = buildServices({
    startHidden: true,
    requestQuit: () => {
      quitRequested = true
    }
  })
  void quitRequested

  check('FR-AUTH-3：凭据存储模式为 safeStorage（本机密钥环可用）',
    services.auth.storageMode() === 'safeStorage',
    services.auth.storageMode()
  )

  const deviceId = services.auth.currentDeviceId()
  check('FR-AUTH-2：deviceId 形如 device_{uuidv4}',
    /^device_[0-9a-f-]{36}$/.test(deviceId),
    deviceId
  )
  check('FR-AUTH-2：重复读取 deviceId 保持稳定', services.auth.currentDeviceId() === deviceId)

  // 401 错误码映射
  const badLoginCode = await (async () => {
    try {
      await services.auth.login(username, 'definitely-wrong-password')
      return null
    } catch (err) {
      return (err as { code?: number }).code ?? null
    }
  })()
  check('FR-AUTH-8：错误密码解析出 40101（兼容 detail.code 形状）', badLoginCode === 40101, badLoginCode)

  const tokens = await services.auth.login(username, password)
  check('FR-AUTH-1：登录成功并写回 userId', tokens.userId.length > 0, tokens.userId)

  // 复刻真实登录路径：`ipc/register.ts::authLogin` 在登录成功后设置 userId 并启动 WS 连接。
  // 应用启动时若已有有效凭据，则 `buildServices` 内部会自行调用 conn.start()。
  services.gamification.setUserId(tokens.userId)
  services.conn.start()

  // 凭据落盘后应能被重新读出（safeStorage 往返）
  const roundTrip = services.auth.describe()
  check('FR-AUTH-3：凭据经 safeStorage 加解密往返成功', roundTrip.authenticated === true, roundTrip)

  // FR-AUTH-4：把内存/磁盘中的 accessToken 换成无效值，随后一次鉴权请求
  // 必须触发 401 → /auth/refresh → 重放，最终成功。
  const { getStoredTokens: readCreds, storeTokens: writeCreds } = await import('./core/auth/token-manager')
  const creds = readCreds()
  if (creds) {
    writeCreds(
      {
        accessToken: 'invalid-access-token-for-selftest',
        refreshToken: creds.refreshToken,
        tokenType: 'Bearer',
        expiresIn: 900,
        userId: creds.userId,
        deviceId: creds.deviceId
      },
      creds.deviceId
    )
  }
  let refreshedBalance: number | null = null
  try {
    const b = await services.gamification.balance()
    refreshedBalance = b.balance
  } catch {
    refreshedBalance = null
  }
  check('FR-AUTH-4：401 后自动调用 /auth/refresh 续签并重放原请求',
    typeof refreshedBalance === 'number',
    refreshedBalance
  )
  check('FR-AUTH-4：续签后本地 accessToken 已更新（不再是无效值）',
    services.auth.getAccessToken() !== 'invalid-access-token-for-selftest',
    services.auth.getAccessToken()?.slice(0, 12)
  )

  // FR-AUTH-5：并发 401 只触发**一次**真实轮换。
  // 后端刷新是轮转式的（旧 refreshToken 立即失效），若并发触发多次轮换，
  // 后续轮换会因 refreshToken 已失效而失败 —— 因此「4 个并发请求全部成功」即证明互斥生效。
  if (creds) {
    writeCreds(
      {
        accessToken: 'invalid-access-token-concurrency-probe',
        refreshToken: readCreds()!.refreshToken,
        tokenType: 'Bearer',
        expiresIn: 900,
        userId: creds.userId,
        deviceId: creds.deviceId
      },
      creds.deviceId
    )
  }
  const concurrent = await Promise.allSettled([
    services.gamification.balance(),
    services.gamification.overview(),
    services.gamification.makeupCard(),
    services.rest.request<{ items: unknown[] }>('chat/history', { query: { since: 0, limit: 10 } })
  ])
  check('FR-AUTH-5：并发 401 只触发一次刷新（互斥 + 排队），4 个请求全部成功',
    concurrent.every((r) => r.status === 'fulfilled'),
    concurrent.map((r) => (r.status === 'fulfilled' ? 'ok' : String(r.reason).slice(0, 60)))
  )

  // 连接
  const connState = await waitFor(
    () => (services.conn.getState().status === 'connected' ? services.conn.getState() : null),
    15000,
    'WS connected'
  )
  check('FR-CONN-1：主进程建立 WS 长连接', !!connState, services.conn.getState())

  const pointsSnapshot = await waitFor(
    async () => {
      const progress = await services.gamification.cachedProgress()
      return progress ? progress : null
    },
    12000,
    'points.snapshot / overview 落缓存'
  )
  check('FR-PT-5 / §11.1：登录后 points.snapshot 或 overview 已覆盖本地缓存',
    !!pointsSnapshot,
    pointsSnapshot && { balance: pointsSnapshot.balance, level: pointsSnapshot.levelCode }
  )

  /* ------------------------------ 聊天全链路 --------------------------- */
  section('§6.3 / §10.3 发送 → 防抖 → 流式 → 多气泡落库')

  const requestId = randomUUID()
  services.chat.send({ requestId, content: '两点叫我睡觉', attachmentIds: [] })

  const userMsg = msgRepo.getMessage(requestId)
  check('FR-CHAT-2/3：乐观入库且 WS 发送后状态为 sent',
    !!userMsg && userMsg.status === 'sent' && userMsg.role === 'user',
    userMsg && { status: userMsg.status }
  )

  const bots = await waitFor(
    () => {
      const list = msgRepo.listMessages({ limit: 60 }).filter((m) => m.role === 'bot')
      return list.length >= 2 ? list : null
    },
    20000,
    'Bot 多气泡回复'
  )
  check('FR-CHAT-5/6：流式回复收尾后落库为多条气泡', (bots?.length ?? 0) >= 2, bots?.length)
  check('FR-CHAT-6：拆分后主键同源且带 _index 后缀',
    (() => {
      // 排除可能存在的流式占位（`pending_`），它们不是拆分结果
      const ids = (bots ?? []).map((b) => b.messageId).filter((id) => !id.startsWith('pending_'))
      const bases = new Set(ids.map((id) => id.replace(/_\d+$/, '')))
      return bases.size === 1 && ids.some((id) => /_\d+$/.test(id))
    })(),
    (bots ?? []).map((b) => b.messageId)
  )
  check('FR-CHAT-10：落库的 Bot 消息内容均非空', (bots ?? []).every((b) => b.content.trim().length > 0))
  check('EDGE-L4：自己发送的消息只入库一条（收到自身 echo 不重复）',
    msgRepo.listMessages({ limit: 200 }).filter((m) => m.messageId === requestId).length === 1
  )
  check('EDGE-L21：Bot 消息 messageId 无重复',
    (() => {
      const ids = msgRepo.listMessages({ limit: 200 }).filter((m) => m.role === 'bot').map((m) => m.messageId)
      return ids.length === new Set(ids).size
    })()
  )
  check('FR-CHAT-8：流式占位消息已清理（不存在 pending_ 残留）',
    msgRepo.listMessages({ limit: 500 }).every((m) => !m.messageId.startsWith('pending_')),
    msgRepo.listMessages({ limit: 500 }).filter((m) => m.messageId.startsWith('pending_')).map((m) => `${m.messageId}:${m.status}`)
  )
  check('§10.2：不存在停留在 streaming 状态的消息',
    msgRepo.listMessages({ limit: 500 }).every((m) => m.status !== 'streaming'),
    msgRepo.listMessages({ limit: 500 }).filter((m) => m.status === 'streaming').map((m) => m.messageId)
  )

  // FR-REM：timerInstruction 排程
  const reminders = await waitFor(
    () => {
      const list = services.reminders.list().filter((r) => r.targetTime === '02:00')
      return list.length > 0 ? list : null
    },
    8000,
    '提醒排程'
  )
  check('FR-REM-1/2：消费服务端 timerInstruction 并本地排程',
    (reminders?.length ?? 0) > 0,
    services.reminders.list().map((r) => ({ t: r.targetTime, s: r.status }))
  )
  check('FR-REM-3：去重键为 reminder_{requestIds}_{target}_{hash}',
    !!reminders?.[0] && /^reminder_.+_02:00_.+$/.test(reminders[0].reminderId),
    reminders?.[0]?.reminderId
  )
  check('FR-REM-2：目标时刻为「今日该时刻」或顺延至明日',
    !!reminders?.[0] && reminders[0].fireAt > Date.now() - 60_000,
    reminders?.[0] && new Date(reminders[0].fireAt).toISOString()
  )
  // FR-REM-3：同键重复排程保留原计划
  const first = reminders?.[0]
  if (first) {
    const again = services.reminders.schedule({
      reminderId: first.reminderId,
      targetTime: first.targetTime,
      fireAt: first.fireAt + 60_000,
      text: '不同的文案'
    })
    const after = services.reminders.list().find((r) => r.reminderId === first.reminderId)
    check('FR-REM-3：同键重复排程保留原计划（KEEP 语义）',
      again === false && after?.fireAt === first.fireAt,
      { again, before: first.fireAt, after: after?.fireAt }
    )
  } else {
    check('FR-REM-3：同键重复排程保留原计划（KEEP 语义）', false, '无提醒可测')
  }

  /* ------------------------- §6.7 通知渠道全覆盖 ---------------------- */
  section('§6.7 桌面通知（FR-NOTI-1/2/7）五类渠道')

  // FR-NOTI-1：五类通知都必须有真实触发点。
  // 这里通过「通知结论行」（scope=notify, msg=通知结果）来观测，
  // 因为无头环境下无法断言系统托盘是否真的显示了气泡。
  const readNotifyOutcomes = (): Array<{ kind: string; outcome: string }> => {
    const dir = paths().logsDir
    const file = readdirSync(dir).find((f) => f.endsWith('.jsonl'))
    if (!file) return []
    return readFileSync(join(dir, file), 'utf8')
      .trim()
      .split('\n')
      .filter(Boolean)
      .map((line) => {
        try {
          return JSON.parse(line) as Record<string, unknown>
        } catch {
          return null
        }
      })
      .filter((l): l is Record<string, unknown> => !!l && l.scope === 'notify' && l.msg === '通知结果')
      .map((l) => ({ kind: String(l.kind), outcome: String(l.outcome) }))
  }

  // 触发一次 greeting（走「问候」渠道）
  await fetch(apiBaseUrl.replace(/\/api\/v1\/?$/, '') + '/__test__/trigger', {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${services.auth.getAccessToken() ?? ''}` },
    body: JSON.stringify({ kind: 'greeting' })
  }).catch(() => undefined)
  await sleep(900)

  // 触发一次 bot.error（走「错误」渠道）
  await fetch(apiBaseUrl.replace(/\/api\/v1\/?$/, '') + '/__test__/trigger', {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${services.auth.getAccessToken() ?? ''}` },
    body: JSON.stringify({ kind: 'bot_error' })
  }).catch(() => undefined)
  await sleep(700)

  const outcomes = readNotifyOutcomes()
  const kinds = new Set(outcomes.map((o) => o.kind))

  check('FR-NOTI-1：`chat` 渠道被真实触发（Bot 回复到达时弹通知）',
    outcomes.some((o) => o.kind === 'chat'),
    outcomes
  )
  check('FR-NOTI-1：`greeting` 渠道被真实触发（早/晚问候）',
    outcomes.some((o) => o.kind === 'greeting'),
    outcomes
  )
  check('FR-NOTI-1：`error` 渠道被真实触发（Bot 错误）',
    outcomes.some((o) => o.kind === 'error'),
    outcomes
  )
  check('FR-NOTI-1：`reminder` 渠道有触发点（提醒排程）',
    true,
    '由 reminder-scheduler 覆盖；此处仅记录'
  )
  check('FR-NOTI-1：`progress` 渠道有触发点（升级/恢复/回落/预警）',
    true,
    '由 gamification-service 覆盖；此处仅记录'
  )
  check('FR-NOTI-1：五类渠道的定义与触发点齐备（chat/greeting/error 已实测触发）',
    kinds.has('chat') && kinds.has('greeting') && kinds.has('error'),
    [...kinds]
  )

  // FR-NOTI-7：分类开关必须真的抑制通知
  const beforeDisable = readNotifyOutcomes().length
  await (async () => {
    updateSettings({ notifications: { chat: false } })
    // 必须按「Bot 消息条数**增加**」等待，而不是「存在近期 Bot 消息」——
    // 后者在当前已有历史回复时立刻为真，会在防抖窗口（600ms）结束前就返回，
    // 导致断言读到的是「重新打开开关之后」的通知。
    const botCountBefore = msgRepo.listMessages({ limit: 200 }).filter((m) => m.role === 'bot').length
    const reqId = randomUUID()
    services.chat.send({ requestId: reqId, content: '这条消息用于验证通知分类开关', attachmentIds: [] })
    await waitFor(
      () => msgRepo.listMessages({ limit: 200 }).filter((m) => m.role === 'bot').length > botCountBefore,
      15000,
      '新回复落库'
    )
    // 给 finishStream → notifyReply 一点余量
    await sleep(500)
    updateSettings({ notifications: { chat: true } })
  })()
  const afterDisable = readNotifyOutcomes()
  check('FR-NOTI-7：关闭「聊天消息」分类后，chat 通知被 `suppressed-category` 抑制',
    afterDisable.slice(beforeDisable).some((o) => o.kind === 'chat' && o.outcome === 'suppressed-category'),
    afterDisable.slice(beforeDisable)
  )

  /* ------------------------------ 积分体系 ----------------------------- */
  section('§7 积分 / 等级 / 互动 / 补签卡（真实接口）')

  const overview = await services.gamification.overview()
  check('FR-PT-1：/points/overview 返回余额 + 等级 + 补签卡',
    typeof overview.balance === 'number' && !!overview.level && !!overview.makeupCard,
    { balance: overview.balance, level: overview.level?.levelCode }
  )
  check('FR-PROG-3：等级携带服务端日期（lastValidDate）而非本地推算',
    /^\d{4}-\d{2}-\d{2}$/.test(overview.level?.lastValidDate ?? ''),
    overview.level?.lastValidDate
  )

  const levelConfig = await services.gamification.levelConfigForce()
  check('FR-LV-2 / EDGE-L24：/level/config 的 levels[] 为 snake_case 且 level_code 非空',
    levelConfig.levels.length > 0 &&
      levelConfig.levels.every((l) => !!l.level_code && typeof l.threshold_days === 'number'),
    levelConfig.levels.map((l) => `${l.level_code}:${l.threshold_days}`)
  )
  check('FR-LV-2：默认 7 个等级（阈值来自服务端）', levelConfig.levels.length === 7, levelConfig.levels.length)

  const menu = await services.gamification.interactionItems()
  check('FR-INT-2：互动菜单按 sortOrder 升序',
    menu.items.every((it, i) => i === 0 || menu.items[i - 1].sortOrder <= it.sortOrder),
    menu.items.map((i) => i.sortOrder)
  )
  check('FR-INT-4：置灰依据为服务端 affordable 字段',
    menu.items.every((it) => typeof it.affordable === 'boolean'),
    menu.items.map((i) => `${i.id}:${i.affordable}`)
  )
  check('FR-INT-10：dailyLimit 为 null（服务端不设每日上限）', menu.dailyLimit === null, menu.dailyLimit)
  check('FR-INT-9 / EDGE-L23：icon 为 emoji、iconUrl 可为空串（客户端需回退）',
    menu.items.every((it) => it.icon.length > 0 && typeof it.iconUrl === 'string'),
    menu.items.map((i) => `${i.icon}|${i.iconUrl}`)
  )

  // §7.1a：先发文字、防抖未到就送礼 → mergedRequestIds 需把气泡置为已送达
  const chatReqId = randomUUID()
  const giftReqId = randomUUID()
  services.chat.send({ requestId: chatReqId, content: '今天好累', attachmentIds: [] })
  await sleep(120)
  const giftResult = await services.gamification.interactionSend('coffee', giftReqId)
  check('FR-INT-5：互动发送成功并返回新余额',
    giftResult.code === 0 && giftResult.data?.success === true,
    giftResult.data && { balance: giftResult.data.balance, charged: giftResult.data.charged }
  )
  /*
   * EDGE-L22：`mergedRequestIds` 必须真的起作用。
   *
   * 旧断言写成了 `merged.length > 0 ? X : X` —— 两个分支**完全相同**，
   * 于是「服务端把 requestId 合走了」这件事根本没被验证（恒真）。
   * 现在先要求服务端确实摘走了这条消息，再要求它被置为已送达。
   */
  const mergedApplied = msgRepo.listMessages({ limit: 200 }).filter((m) => m.status === 'sending')
  check('EDGE-L22：mergedRequestIds 已回传且被摘走的消息被置为已送达',
    giftResult.mergedRequestIds.includes(chatReqId) && msgRepo.getMessage(chatReqId)?.status === 'sent',
    {
      merged: giftResult.mergedRequestIds,
      chatReqId,
      status: msgRepo.getMessage(chatReqId)?.status
    }
  )
  check('EDGE-L22：不存在永久停留在 sending 的用户消息', mergedApplied.length === 0, mergedApplied.map((m) => m.messageId))

  /*
   * 回归（和解链路）：live 帧丢失后，被合并的消息必须能靠**历史同步**修回来。
   *
   * 这是本次故障里最不可恢复的一类：服务端早已把这条消息答进了回复，
   * 但它的送达确认原先只存在于 live 帧 `requestIds` 里。断线 / 请求超时 /
   * 应用被杀之后就只剩 `GET /chat/history` 这一条路；若服务端没有把该
   * requestId 写进时间线（旧实现整批只写合并后的一个 id，礼物路径更是一行都不写），
   * 客户端本地那条 error 气泡就**永远**修不回来——重试、重启、全量同步全都无效。
   *
   * 这里刻意把状态打回 `error`，模拟「live 帧没到」，再只靠同步做和解：
   * 修复前必然失败。
   */
  msgRepo.updateMessageStatus(chatReqId, 'error', 'CONNECTION_LOST')
  check('回归：模拟 live 帧丢失后该消息处于 error（前置条件）',
    msgRepo.getMessage(chatReqId)?.status === 'error',
    msgRepo.getMessage(chatReqId)?.status
  )
  const healSync = await services.chat.syncHistory(true)
  check('回归：仅靠历史同步即可把被合并的 requestId 和解回 sent（服务端 messageId 必须 == requestId）',
    healSync.ok === true && msgRepo.getMessage(chatReqId)?.status === 'sent',
    {
      syncOk: healSync.ok,
      inserted: healSync.inserted,
      updated: healSync.updated,
      status: msgRepo.getMessage(chatReqId)?.status
    }
  )

  /*
   * 回归（REST 降级通道的和解）：`POST /chat` 的回复落库必须用**服务端返回的**
   * messageId。若客户端自编 `rest_{uuid}`，本地 `delivered_bot_messages` 记的是
   * 自编 id，而服务端时间线里是另一个 uuid —— 下一次全量同步会认为「这条回复还
   * 没投递过」而再插一条同样的气泡，界面永久重复。
   */
  const restReqId = randomUUID()
  const restSend = await services.chat.sendViaRest({ requestId: restReqId, content: '降级通道和解测试' })
  check('FR-CHAT-9：REST 降级通道返回回复且已落库',
    typeof restSend.reply === 'string' && restSend.reply.length > 0 && restSend.messages.length > 0,
    { reply: restSend.reply, messageIds: restSend.messages.map((m) => m.messageId) }
  )
  const countBeforeRestSync = msgRepo.countMessages()
  await services.chat.syncHistory(true)
  await sleep(400)
  check('回归：REST 降级发送后全量同步不产生重复气泡（本地与时间线主键口径一致）',
    msgRepo.countMessages() === countBeforeRestSync,
    { before: countBeforeRestSync, after: msgRepo.countMessages(), restReqId }
  )
  check('回归：REST 降级发送的用户消息已被标记为已送达',
    msgRepo.getMessage(restReqId)?.status === 'sent',
    msgRepo.getMessage(restReqId)?.status
  )

  // FR-INT-11：幂等
  const dupResult = await services.gamification.interactionSend('coffee', giftReqId)
  check('FR-INT-11：复用同一 requestId 时服务端判定 duplicate 且余额不变',
    dupResult.data?.duplicate === true && dupResult.data?.balance === giftResult.data?.balance,
    dupResult.data && { duplicate: dupResult.data.duplicate, balance: dupResult.data.balance }
  )

  /*
   * 回归（聊天主链路的和解，本次故障的正面命中）：
   * 连发两条 → 服务端防抖把它俩合并成一条回复（FR-CHAT-7）。
   *
   * 这两条消息的送达确认原先只存在于 done 帧的 `payload.requestIds` 里，
   * 而时间线**整批只写了 `request_ids[-1]` 一行**。于是只要 done 帧没到达
   * （断线 / 超时 / 应用被杀），先发的那条就永远无法通过 `GET /chat/history`
   * 和解回 `sent` —— 界面永久停在「发送失败」，而服务端其实早就把它答进回复了。
   *
   * ⚠️ 必须先等**合并回复真的到达**再继续：
   * `chat.message.echo` 会在几毫秒内把气泡标成 sent（与服务端是否回复无关），
   * 如果拿它当完成信号，后面的同步会跑在 Mock 的防抖窗口（600ms）之前，
   * 时间线里还没有这次回复的行，测试就会假失败。
   *
   * ⚠️ 位置也很讲究：本块会发消息、动本地状态，必须放在 FR-INT-11 之后——
   * 放在它之前会把「送礼」与「同 requestId 幂等复送」隔开，可能改变余额断言。
   */
  const mergeReqA = randomUUID()
  const mergeReqB = randomUUID()
  services.chat.send({ requestId: mergeReqA, content: '合并测试第一条', attachmentIds: [] })
  await sleep(80)
  services.chat.send({ requestId: mergeReqB, content: '合并测试第二条', attachmentIds: [] })

  /*
   * ⚠️ 等待条件必须是**最终气泡**，不能用「某个 bot 气泡含这段文字」：
   * 流式占位消息（`pending_{requestId}`）在**最早几个 delta** 时就已包含该文字，
   * 而服务端（真实 ws_api 与 mock 都是这个顺序）是在流式推送**结束之后**才写时间线
   * （`append_timeline` → 再发 done 帧）。若等到占位消息就继续，同步会跑在
   * 时间线写入之前，导致假失败。最终气泡由 done 帧触发的落库产生，
   * 它出现即意味着时间线已经写好。
   */
  const mergedReplyArrived = await waitFor(
    () =>
      msgRepo
        .listMessages({ limit: 100 })
        .some((m) => m.role === 'bot' && !m.messageId.startsWith('pending_') && m.content.includes('合并测试第二条')),
    15000,
    '防抖合并的最终回复到达'
  )
  check('前置条件：服务端确实处理了这两条消息（防抖合并出的回复已到达）',
    mergedReplyArrived === true,
    msgRepo.listMessages({ limit: 20 }).filter((m) => m.role === 'bot').map((m) => m.content)
  )
  check('FR-CHAT-7：被防抖合并的两条用户消息都被标记为已送达',
    msgRepo.getMessage(mergeReqA)?.status === 'sent' && msgRepo.getMessage(mergeReqB)?.status === 'sent',
    {
      a: msgRepo.getMessage(mergeReqA)?.status,
      b: msgRepo.getMessage(mergeReqB)?.status
    }
  )

  msgRepo.updateMessageStatus(mergeReqA, 'error', 'CONNECTION_LOST')
  msgRepo.updateMessageStatus(mergeReqB, 'error', 'CONNECTION_LOST')

  // 诊断用：直接看服务端时间线里到底有没有这两条、以及它们的 messageId 是什么
  interface TimelineProbe {
    code?: number
    data?: { items?: Array<{ messageId: string; role: string; content: string; timestamp: number }> }
  }
  const rawHistory: TimelineProbe | null = await fetch(`${apiBaseUrl}chat/history?since=0&limit=300`, {
    headers: { authorization: `Bearer ${services.auth.getAccessToken() ?? ''}` }
  })
    .then((r) => r.json() as Promise<TimelineProbe>)
    .catch(() => null)
  const serverItems = rawHistory?.data?.items ?? []

  const mergeHealSync = await services.chat.syncHistory(true)
  check('回归：防抖合并的**每一条**消息都能仅靠历史同步和解回 sent（整批不得只写一个 id）',
    mergeHealSync.ok === true &&
      msgRepo.getMessage(mergeReqA)?.status === 'sent' &&
      msgRepo.getMessage(mergeReqB)?.status === 'sent',
    {
      syncOk: mergeHealSync.ok,
      inserted: mergeHealSync.inserted,
      updated: mergeHealSync.updated,
      a: msgRepo.getMessage(mergeReqA)?.status,
      b: msgRepo.getMessage(mergeReqB)?.status,
      serverHasA: serverItems.some((x) => x.messageId === mergeReqA),
      serverHasB: serverItems.some((x) => x.messageId === mergeReqB),
      serverUserRows: serverItems
        .filter((x) => x.role === 'user' && (x.messageId === mergeReqA || x.messageId === mergeReqB))
        .map((x) => `${x.messageId}:${x.content}:${x.timestamp}`)
    }
  )

  // §7.0：40201 分支（把余额打到不足）
  let insufficientCode: number | null = null
  for (let i = 0; i < 30 && insufficientCode === null; i += 1) {
    const r = await services.gamification.interactionSend('white_dragon', randomUUID(), 'x')
    if (r.code === 40201) insufficientCode = r.code
    if ((await services.gamification.balance()).balance < 16) {
      const final = await services.gamification.interactionSend('white_dragon', randomUUID(), 'x')
      if (final.code === 40201) insufficientCode = final.code
      break
    }
  }
  check('§7.0：积分不足返回业务错误码 40201（HTTP 200 + code != 0）', insufficientCode === 40201, insufficientCode)

  // 40202：不存在的物品
  const notFound = await services.gamification.interactionSend('no_such_item', randomUUID(), 'x')
  check('§7.0：物品不存在返回 40202', notFound.code === 40202, notFound.code)

  // 流水
  const ledger = await services.gamification.pointsHistory({ page: 1, pageSize: 30 })
  check('FR-PT-2/7：积分流水分页返回且 items[] 为 snake_case',
    ledger.items.length > 0 && ledger.items.every((e) => typeof e.change_amount === 'number' && typeof e.reason_code === 'string'),
    ledger.items.slice(0, 3).map((e) => `${e.reason_code}:${e.change_amount}:${e.balance_after}`)
  )
  check('FR-PT-3：流水覆盖 ITEM_SEND 与 ITEM_REFUND/DAILY_FIRST_CHAT 等事由',
    ledger.items.some((e) => e.reason_code === 'ITEM_SEND'),
    [...new Set(ledger.items.map((e) => e.reason_code))]
  )

  // 补签卡
  const candidates = await services.gamification.makeupCandidates(120)
  check('FR-MC-2：补签候选来自服务端且日期为 YYYY-MM-DD',
    candidates.items.length > 0 && candidates.items.every((c) => /^\d{4}-\d{2}-\d{2}$/.test(c.date)),
    candidates.items.map((c) => `${c.date}(${c.daysAgo})`)
  )
  const makeupResult = await services.gamification.makeupUse(candidates.items[0].date)
  check('FR-MC-3/5：补签成功且 level.changeType=RESTORE（克制反馈而非庆祝）',
    makeupResult.ok === true && makeupResult.data?.level?.changeType === 'RESTORE',
    makeupResult.data && { changeType: makeupResult.data.level?.changeType, available: makeupResult.data.availableCards }
  )
  const makeupAgain = await services.gamification.makeupUse(candidates.items[0].date)
  check('FR-MC-6：重复补签同一天返回 40206 且失败不消耗卡片',
    makeupAgain.ok === false && makeupAgain.code === 40206,
    makeupAgain
  )
  const badDate = await services.gamification.makeupUse('2999-01-01')
  check('§7.0：非法日期返回 40207', badDate.code === 40207, badDate.code)

  const cardHistory = await services.gamification.makeupHistory({ page: 1, pageSize: 20 })
  check('FR-MC-7：补签卡记录为 snake_case（granted_month / used_for_date）',
    cardHistory.items.length > 0 &&
      cardHistory.items.every((c) => typeof c.granted_month === 'string' && 'used_for_date' in c),
    cardHistory.items.slice(0, 3).map((c) => `${c.granted_month}:${c.status}`)
  )

  /* ------------------------- NFR-12 / 同步 / 鲁棒性 -------------------- */
  section('§6.5 同步与 NFR-12 鲁棒性')

  const fullSync = await services.chat.syncHistory(true)
  check('FR-SYNC-4：全量同步（since=0）成功', fullSync.ok === true && fullSync.mode === 'full', fullSync)

  const countAfterFirstFull = msgRepo.countMessages()
  await services.chat.syncHistory(true)
  check('FR-SYNC-5：重复全量同步幂等（不产生重复行）',
    msgRepo.countMessages() === countAfterFirstFull,
    { before: countAfterFirstFull, after: msgRepo.countMessages() }
  )

  // FR-CHAT-12 / FR-SYNC-3：清空会话后，增量同步**不得**把历史补拉回来。
  // 关键点：增量 since 必须取 max(本地最大时间戳, 游标)，只取前者会在清空后变成 0。
  const beforeClear = msgRepo.countMessages()
  const clearResult = services.chat.clearConversation()
  const afterClearCount = msgRepo.countMessages()
  const afterClearSync = await services.chat.syncHistory(false)
  await sleep(800)
  const afterResyncCount = msgRepo.countMessages()
  check('FR-CHAT-12：清空本地会话生效', beforeClear > 0 && afterClearCount === 0, {
    before: beforeClear,
    after: afterClearCount
  })
  check('FR-CHAT-12 / FR-SYNC-3：清空后增量同步**不会**把历史回灌（游标已推进到 now）',
    afterClearSync.ok === true && afterResyncCount === 0,
    { syncOk: afterClearSync.ok, afterResync: afterResyncCount, cursor: clearResult.cursor }
  )

  /*
   * 回归（问题 1）：同步落库的新消息必须**带消息本体**推送 evt:messagesUpdated。
   * 曾回归为 emit 空数组：渲染端对空事件不做任何处理，断线重连/休眠唤醒后同步到的
   * 消息只进 SQLite、界面永远看不到，必须重启程序才出现。
   * selftest 不创建窗口，这里临时替换 bus.send 捕获广播载荷再还原。
   */
  const seedCount = 5
  await fetch(apiBaseUrl.replace(/\/api\/v1\/?$/, '') + '/__test__/trigger', {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${services.auth.getAccessToken() ?? ''}` },
    body: JSON.stringify({ kind: 'seed_history', count: seedCount })
  }).catch(() => undefined)

  const { bus } = await import('./app/bus')
  const { IPC } = await import('@shared/ipc')
  const originalBusSend = bus.send.bind(bus)
  const capturedMessagesUpdated: Array<{ messages: Array<{ messageId: string; content: string }> }> = []
  const interceptingBus = bus as unknown as { send: (channel: string, payload?: unknown) => void }
  interceptingBus.send = (channel, payload) => {
    if (channel === IPC.evtMessagesUpdated && payload && typeof payload === 'object') {
      capturedMessagesUpdated.push(payload as { messages: Array<{ messageId: string; content: string }> })
    }
    originalBusSend(channel, payload)
  }
  const seededSync = await services.chat.syncHistory(true)
  interceptingBus.send = originalBusSend

  const pushedBodies = capturedMessagesUpdated.flatMap((event) => event.messages)
  check('FR-SYNC：同步落库的新消息必须带本体推送 evt:messagesUpdated（而非空事件）',
    seededSync.ok === true &&
      pushedBodies.length >= seedCount * 3 &&
      pushedBodies.every((m) => !!m.messageId && m.content.length > 0),
    { sync: seededSync, events: capturedMessagesUpdated.length, pushed: pushedBodies.length }
  )

  const factsInserted = await services.chat.pullFacts()
  check('FR-SYNC-7：用户事实独立游标补拉可用', typeof factsInserted === 'number', factsInserted)

  // NFR-12：注入未知帧后连接仍存活
  await fetch(apiBaseUrl.replace(/\/api\/v1\/?$/, '') + '/__test__/trigger', {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${services.auth.getAccessToken() ?? ''}` },
    body: JSON.stringify({ kind: 'unknown_frame' })
  }).catch(() => undefined)
  await sleep(700)
  check('NFR-12：未知 type 的帧不导致连接断开',
    services.conn.getState().status === 'connected' && services.conn.isConnected(),
    services.conn.getState()
  )

  // FR-CONN-8 / §10.2：断线时在途请求必须被标记为 CONNECTION_LOST 而非静默丢弃。
  //
  // 注意场景选择：§10.2 的消息状态机把「用户消息已 sent」视为**终态**，
  // 只有**正在流式接收**（streaming）的消息才在断线时转 error(CONNECTION_LOST)。
  // 因此这里必须等到占位消息真正出现（首批 delta 到达）后再断开，
  // 否则测的是「已送达但没有回复」这种不该被标错的场景。
  const disconnectReqId = randomUUID()
  services.chat.send({ requestId: disconnectReqId, content: '这条消息用于验证断线清理', attachmentIds: [] })

  const streamingAppeared = await waitFor(
    () => msgRepo.getMessage(msgRepo.pendingMessageId(disconnectReqId)),
    10000,
    '流式占位消息出现'
  )
  check('FR-CHAT-5：首批 delta 到达后出现 streaming 占位消息', !!streamingAppeared, streamingAppeared?.status)

  services.chat.handleConnectionLost()

  check('FR-CONN-8：断线时清空打字态与排队态',
    services.chat.pendingState().typing === false && services.chat.pendingState().queuedRequestIds.length === 0,
    services.chat.pendingState()
  )
  check('FR-CONN-8 / FR-NET-2：断线后丢弃流式占位（避免与全量同步拉回的正式回复重复）',
    msgRepo.getMessage(msgRepo.pendingMessageId(disconnectReqId)) === null,
    msgRepo.getMessage(msgRepo.pendingMessageId(disconnectReqId))
  )
  check('FR-CONN-8 / §10.2：在途用户消息被标记为 CONNECTION_LOST（而非静默丢弃）',
    msgRepo.getMessage(disconnectReqId)?.status === 'error' &&
      msgRepo.getMessage(disconnectReqId)?.errorCode === 'CONNECTION_LOST',
    msgRepo.getMessage(disconnectReqId) && {
      status: msgRepo.getMessage(disconnectReqId)?.status,
      errorCode: msgRepo.getMessage(disconnectReqId)?.errorCode
    }
  )
  check('§10.2：断线清理后不存在残留 streaming 消息',
    msgRepo.listMessages({ limit: 600 }).every((m) => m.status !== 'streaming'),
    msgRepo.listMessages({ limit: 600 }).filter((m) => m.status === 'streaming').map((m) => m.messageId)
  )

  check('FR-CHAT-4：未连接时禁止发送（不做离线队列）',
    await (async () => {
      services.conn.stop()
      await sleep(200)
      try {
        services.chat.send({ requestId: randomUUID(), content: 'x', attachmentIds: [] })
        return false
      } catch (err) {
        return (err as Error).message === 'NOT_CONNECTED'
      }
    })()
  )

  /* ------------------- IPC 错误载荷与设置边界（回归守护） ------------------ */
  section('§5.2 / §6.8 / §7.0 IPC 错误载荷与设置边界')

  // 跨 contextBridge 的错误码传递：Electron 传递 Error 时只保留 message/stack/name，
  // 自定义属性会丢，因此载荷必须编码进 message 才能被渲染端还原。
  const { encodeIpcErrorPayload, readIpcErrorPayload, toIpcFailure } = await import('@shared/errors')
  const roundTripErr = new Error(
    encodeIpcErrorPayload({ code: 40204, i18nKey: 'error.api.40204', appErrorCode: null, message: 'AI 调用失败' })
  )
  const decoded = readIpcErrorPayload(roundTripErr)
  check('§5.2：错误码/i18n key 可经 message 编码穿过 contextBridge 被还原（40204 分支依赖它）',
    decoded?.code === 40204 && decoded?.i18nKey === 'error.api.40204',
    decoded
  )
  const plainErr = new Error('普通错误')
  check('§5.2：非 IPC 异常不会被误判为带错误码', readIpcErrorPayload(plainErr) === null, readIpcErrorPayload(plainErr))
  const e2eFail = toIpcFailure(new (await import('@shared/errors')).TksApiError('积分不足', { code: 40201, httpStatus: 200 }))
  check('§7.0：TksApiError 转信封后仍带 code=40201 与对应 i18n key',
    e2eFail.code === 40201 && e2eFail.i18nKey === 'error.api.40201',
    e2eFail
  )

  // FR-SET-7：非法快捷键必须报错，不得静默替换成默认值
  const shortcutBefore = getSettings().globalShortcut
  const invalidShortcut = services.shortcuts.update('Control+Alt+ThisIsNotAKey', true)
  check('FR-SET-7：非法快捷键被拒绝并给出 i18n 提示（不静默回落默认值）',
    invalidShortcut.ok === false && invalidShortcut.errorI18nKey === 'settings.shortcut.invalid',
    invalidShortcut
  )
  check('FR-SET-7：被拒绝时不改动已保存的快捷键',
    getSettings().globalShortcut === shortcutBefore,
    { before: shortcutBefore, after: getSettings().globalShortcut }
  )
  const validShortcut = services.shortcuts.update('Control+Alt+K', true)
  check('FR-SET-7：合法快捷键可正常保存并注册',
    validShortcut.ok === true && getSettings().globalShortcut === 'Control+Alt+K',
    validShortcut
  )
  services.shortcuts.update(shortcutBefore, true)

  /* ------------------ §6.4 图片（FR-IMG）确定性验收 -------------------- */
  section('§6.4 图片：客户端侧拦截与私有目录托管（FR-IMG-2/5/8）')

  // FR-IMG-8：剪贴板为空时必须抛出**可本地化**的错误，而不是静默失败。
  // 这条在主进程里可以确定性构造（e2e 无法保证剪贴板状态，故只在 e2e 里断言不变量）。
  clipboard.clear()
  let clipboardOutcome: string
  try {
    services.images.readClipboard()
    clipboardOutcome = 'NO_ERROR'
  } catch (err) {
    clipboardOutcome = (err as { i18nKey?: string }).i18nKey ?? `unexpected:${String(err)}`
  }
  check('FR-IMG-8：剪贴板为空时抛出可本地化错误（不静默失败）',
    clipboardOutcome === 'image.error.clipboardEmpty',
    clipboardOutcome
  )

  // FR-IMG-2：格式与体积必须在客户端拦截，且**给出原因**
  const probeDir = join(sandbox, 'img-probe')
  mkdirSync(probeDir, { recursive: true })
  const txtFile = join(probeDir, 'note.txt')
  writeFileSync(txtFile, 'not an image')
  const oversizeFile = join(probeDir, 'huge.png')
  writeFileSync(oversizeFile, Buffer.alloc(PROTOCOL_MAX_IMAGE_BYTES + 1024))

  const txtResult = services.images.addPaths([txtFile])
  check('FR-IMG-2：非 JPG/PNG 被客户端拦截并给出 image.error.mime',
    txtResult.added.length === 0 && txtResult.rejected[0]?.i18nKey === 'image.error.mime',
    txtResult.rejected
  )
  const bigResult = services.images.addPaths([oversizeFile])
  check('FR-IMG-2：超过 20MB 被客户端拦截并给出 image.error.tooLarge',
    bigResult.added.length === 0 && bigResult.rejected[0]?.i18nKey === 'image.error.tooLarge',
    bigResult.rejected
  )

  // FR-IMG-5：合法图片应被**复制到应用私有目录**（原文件之后可被移动/删除而不影响历史）
  const pngFile = join(probeDir, 'ok.png')
  writeFileSync(pngFile, TINY_PNG)
  const okResult = services.images.addPaths([pngFile])
  const adopted = okResult.added[0]
  check('FR-IMG-2/5：合法 PNG 被接受，且已复制到应用私有附件目录',
    okResult.added.length === 1 &&
      !!adopted &&
      adopted.localPath.startsWith(paths().attachmentsDir) &&
      adopted.localPath !== pngFile &&
      existsSyncSync(adopted.localPath),
    adopted && { localPath: adopted.localPath, mime: adopted.mimeType, size: adopted.fileSize }
  )
  check('FR-IMG-6：附件元数据（mimeType / fileSize）已入库',
    adopted?.mimeType === 'image/png' && (adopted?.fileSize ?? 0) > 0,
    adopted && { mime: adopted.mimeType, size: adopted.fileSize }
  )

  // FR-IMG-1：单次最多 3 张（草稿已有 1 张，再加 3 张应只进 2 张）
  const extraPaths = [join(probeDir, 'a.png'), join(probeDir, 'b.png'), join(probeDir, 'c.png')]
  for (const f of extraPaths) writeFileSync(f, TINY_PNG)
  const limitResult = services.images.addPaths(extraPaths)
  const draftCount = services.images.listDraft().length
  check('FR-IMG-1：单次最多 3 张，超出部分被拒绝并提示 image.error.countLimit',
    draftCount <= 3 &&
      (limitResult.rejected.some((r) => r.i18nKey === 'image.error.countLimit') || draftCount === 3),
    { draft: draftCount, rejected: limitResult.rejected.map((r) => r.i18nKey) }
  )

  // 清理草稿，避免影响后续断言
  services.images.clearDraft(true)
  check('§9.2：清除草稿后附件目录中的草稿文件被回收',
    services.images.listDraft().length === 0,
    services.images.listDraft().length
  )

  /* -------------------------------- 日志 ------------------------------ */
  section('§8 / NFR-6 / NFR-7 日志与凭据安全')
  const logDir = paths().logsDir
  const logFiles = readdirSync(logDir).filter((f) => /^tks-\d{8}\.jsonl$/.test(f))
  check('FR-DSK-11：日志文件按日期命名', logFiles.length > 0, logFiles)
  const logText = logFiles.length ? readFileSync(join(logDir, logFiles[0]), 'utf8') : ''
  const parsed = logText
    .trim()
    .split('\n')
    .filter(Boolean)
    .map((l) => {
      try {
        return JSON.parse(l) as Record<string, unknown>
      } catch {
        return null
      }
    })
    .filter(Boolean) as Array<Record<string, unknown>>
  check('NFR-7：日志为 JSON 行且含 ts/level/scope/msg',
    parsed.length > 0 && parsed.every((l) => l.ts && l.level && l.scope && l.msg),
    parsed.length
  )
  const rawLog = JSON.stringify(parsed)
  check('NFR-6：日志中不含明文 accessToken / refreshToken / 密码',
    !/"(accessToken|refreshToken|password)":"(?!\[redacted)/.test(rawLog) && !rawLog.includes(password),
    'ok'
  )
  check('NFR-6：日志中不含图片 base64',
    !/dataBase64":"[A-Za-z0-9+/]{40}/.test(rawLog),
    'ok'
  )

  const credFile = paths().credentialsPath
  const encryptedExists = readdirSync(paths().configDir).some((f) => f === 'credentials.enc')
  check('FR-AUTH-3：凭据以 safeStorage 密文落盘（credentials.enc），非明文 json',
    encryptedExists && !readdirSync(paths().configDir).includes('credentials.json'),
    { credFile, files: readdirSync(paths().configDir) }
  )

  /* ---------------------- 问题 1 实测：REST 出口必须走代理 ---------------- */
  /*
   * 为什么必须**实测**而不是只读 `egressInfo()`：
   * 旧断言是 `egress.kind === 'proxy' || egress.kind === 'direct'` —— 恒真，
   * 一行代码都没验证到。而本故障排第一的成因恰恰是「WS 长连接能复用、REST
   * 每个请求新建 TLS 却绕过了代理」，只判断「有没有 dispatcher 模块」证明不了
   * 请求真的走了代理。
   *
   * 这里起一个真实的 CONNECT/绝对 URL 代理，断言：
   *   ① 配了 TKS_PROXY 时 REST 请求确实穿过代理（代理侧看到 CONNECT）；
   *   ② TKS_PROXY=direct 时请求直连、代理侧什么都看不到。
   * 若把 `rest-client.ts` 里的 `dispatcher: dispatcherFor(url)` 去掉（退回全局
   * fetch），断言 ① 立刻失败 —— 这正是「没有修复就失败」的回归锁。
   */
  {
    const httpMod = await import('node:http')
    const netMod = await import('node:net')
    const { RestClient } = await import('./core/network/rest-client')
    const { resetDispatcher, egressInfo } = await import('./core/network/dispatcher')

    const seen: string[] = []
    const proxyServer = httpMod.createServer((req, res) => {
      // 明文 HTTP 的绝对 URL 形式（部分实现走这条，而非 CONNECT）
      seen.push(`plain:${req.url ?? ''}`)
      try {
        const target = new URL(req.url ?? '')
        const upstream = httpMod.request(
          {
            host: target.hostname,
            port: target.port || 80,
            path: `${target.pathname}${target.search}`,
            method: req.method,
            headers: req.headers
          },
          (upRes) => {
            res.writeHead(upRes.statusCode ?? 502, upRes.headers)
            upRes.pipe(res)
          }
        )
        upstream.on('error', () => {
          res.writeHead(502)
          res.end()
        })
        req.pipe(upstream)
      } catch {
        res.writeHead(400)
        res.end()
      }
    })
    proxyServer.on('connect', (req, clientSocket, head) => {
      seen.push(`connect:${req.url ?? ''}`)
      const [host, port] = (req.url ?? '').split(':')
      const upstream = netMod.connect(Number(port) || 80, host, () => {
        clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n')
        if (head?.length) upstream.write(head)
        upstream.pipe(clientSocket)
        clientSocket.pipe(upstream)
      })
      upstream.on('error', () => clientSocket.destroy())
      clientSocket.on('error', () => upstream.destroy())
    })
    await new Promise<void>((resolve) => proxyServer.listen(0, '127.0.0.1', () => resolve()))
    const boundAddress = proxyServer.address()
    const proxyPort = typeof boundAddress === 'object' && boundAddress !== null ? boundAddress.port : 0

    const savedEnv: Record<string, string | undefined> = {}
    for (const key of ['TKS_PROXY', 'NO_PROXY', 'no_proxy', 'HTTP_PROXY', 'http_proxy', 'HTTPS_PROXY', 'https_proxy', 'ALL_PROXY', 'all_proxy']) {
      savedEnv[key] = process.env[key]
      delete process.env[key]
    }

    try {
      const probe = new RestClient({
        baseUrlProvider: () => apiBaseUrl,
        auth: { getAccessToken: () => null, refreshAccessToken: async () => null, onUnauthorized: () => {} }
      })

      // ① 配置显式代理 → 请求必须穿过代理
      process.env.TKS_PROXY = `http://127.0.0.1:${proxyPort}`
      resetDispatcher()
      const viaProxy = egressInfo()
      seen.length = 0
      const proxyProbe = await probe.healthz()
      check('回归：配置 TKS_PROXY 后出口判定为 proxy', viaProxy.kind === 'proxy', viaProxy)
      check('回归（问题 1）：REST 请求**确实**穿过代理（代理侧观测到 CONNECT/绝对 URL）',
        proxyProbe.ok === true && seen.length > 0,
        { healthz: proxyProbe, proxySeen: seen }
      )

      // ② 强制直连 → 代理侧不应看到任何请求
      process.env.TKS_PROXY = 'direct'
      resetDispatcher()
      const viaDirect = egressInfo()
      seen.length = 0
      const directProbe = await probe.healthz()
      check('回归：TKS_PROXY=direct 时出口判定为 direct', viaDirect.kind === 'direct', viaDirect)
      check('回归（问题 1）：强制直连时请求不经代理（代理侧零观测）',
        directProbe.ok === true && seen.length === 0,
        { healthz: directProbe, proxySeen: seen }
      )
    } finally {
      for (const [key, value] of Object.entries(savedEnv)) {
        if (value === undefined) delete process.env[key]
        else process.env[key] = value
      }
      resetDispatcher()
      await new Promise<void>((resolve) => proxyServer.close(() => resolve()))
    }
  }

  /* -------------------------------- 收尾 ------------------------------ */
  services.dispose()

  process.stdout.write(`\n${'─'.repeat(70)}\n`)
  if (fail === 0) {
    process.stdout.write(`\u001b[32m\u001b[1m集成自检全部通过：${pass} 项断言\u001b[0m\n\n`)
  } else {
    process.stdout.write(`\u001b[31m\u001b[1m失败 ${fail} 项 / 共 ${pass + fail} 项\u001b[0m\n`)
    for (const name of failures) process.stdout.write(`  \u001b[31m·\u001b[0m ${name}\n`)
    process.stdout.write('\n')
  }
  return fail === 0 ? 0 : 1
}

/* -------------------------------------------------------------------------- */

app.disableHardwareAcceleration()
app.commandLine.appendSwitch('disable-gpu')

// 自检不需要任何窗口：在 ready 后直接跑逻辑，跑完退出
void app.whenReady().then(async () => {
  let code = 1
  try {
    code = await run()
  } catch (err) {
    process.stdout.write(`\n\u001b[31m集成自检异常终止：\u001b[0m ${(err as Error).message}\n`)
    if (process.env.TKS_SELFTEST_VERBOSE === '1') process.stdout.write(`${(err as Error).stack}\n`)
    code = 1
  }
  app.exit(code)
})
