#!/usr/bin/env node
/**
 * TKS 契约一致的 Mock 后端（仅用于本地联调与验收测试）。
 *
 * 严格按 PRD《TKS_Linux桌面客户端_PRD_v1.md》v1.2 §5（REST/WS 契约）与
 * §7.0（积分体系接口与字段名契约）实现，用于在没有生产账号的情况下端到端验证客户端。
 *
 * 与真实后端的**刻意对齐点**（这些是客户端最容易写错的地方）：
 *  1. `/auth/login`、`/auth/refresh` **无信封**，直接返回对象
 *  2. `/chat/history` 等 `http_api` 接口**有信封**；鉴权失败返回
 *     `{"detail":{"code":40101,...}}`（高层 `code` 不在顶层）——FastAPI 的二次包装形状
 *  3. 积分路由业务失败返回 **HTTP 200 + `code != 0`**（如 40201 / 40202 / 40204）
 *  4. `/level/config` 的 `levels[]`、`/points/history` 的 `items[]`、
 *     `/points/makeup-card*` 的卡记录为 **snake_case**，其余为 camelCase
 *  5. WS 鉴权只认请求头 `Authorization: Bearer`（query token 被拒绝）
 *  6. WS `chat.message` 的 `requestId` 读**顶层**字段
 *  7. 无效 token：先 accept 再下发 `auth.expired`，随后以 code 4001 关闭
 *  8. `pong` 是**顶层** `{type,timestamp}`，无 payload 包装
 *  9. `/healthz` 与 `/api/v1/healthz` 均可，且**只保证 HTTP 200**
 * 10. `chat.reply.stream` 的 done 帧含 `finalContent`（多行，用于验证客户端多气泡拆分）
 *
 * 用法：
 *   node mock-server/server.mjs                 # 默认 http://127.0.0.1:8787
 *   PORT=9000 USER=kris PASS=taki node mock-server/server.mjs
 *   MERGE_WAIT_MS=300 node mock-server/server.mjs   # 缩短「文字+送礼」合并等待
 */

import { createServer } from 'node:http'
import { randomUUID } from 'node:crypto'
import { WebSocketServer } from 'ws'

const PORT = Number(process.env.PORT ?? 8787)
const HOST = process.env.HOST ?? '127.0.0.1'
const USER = process.env.USERNAME_FOR_LOGIN ?? 'kris'
const PASS = process.env.PASSWORD_FOR_LOGIN ?? 'taki'
const USER_ID = process.env.USER_ID ?? 'kris'
const DEVICE_WHITELIST = (process.env.DEVICE_WHITELIST ?? '').split(',').filter(Boolean)
const MERGE_WAIT_MS = Number(process.env.MERGE_WAIT_MS ?? 1200)
const ACCESS_TTL_SEC = Number(process.env.ACCESS_TTL_SEC ?? 900)
/** 设为正数可模拟运维配置了设备白名单（触发 40301）。 */
const KICK_ON_LOGIN = process.env.KICK_ON_LOGIN === '1'
/** 设为 1 时模拟 `AUTH_MAX_DEVICES>0` 的静默顶号：第二次登录使第一个 token 失效。 */
const FAIL_INTERACTION = process.env.FAIL_INTERACTION === '1'

/* -------------------------------------------------------------------------- */
/* 内存状态                                                                    */
/* -------------------------------------------------------------------------- */

const state = {
  /** accessToken → { userId, deviceId } */
  accessTokens: new Map(),
  /** refreshToken → { userId, deviceId } */
  refreshTokens: new Map(),
  balance: 26,
  ledger: [],
  ledgerSeq: 1,
  history: [],
  facts: [],
  /** userId → [{requestId, content}] 防抖缓冲 */
  messageBuffer: new Map(),
  /** userId → Set<WebSocket> */
  connections: new Map(),
  level: {
    levelCode: 'PANDA_LV3',
    levelName: '竹林新秀',
    prevLevelCode: 'PANDA_LV2',
    continuousDays: 18,
    changeType: null,
    changeSource: 'ACTIVITY',
    highestLevelCode: 'PANDA_LV3',
    lastValidDate: isoDate(0),
    gapDays: 0,
    breakDeadlineDate: isoDate(3),
    nextLevelCode: 'PANDA_LV4',
    nextLevelName: '黑白骑士',
    nextLevelThresholdDays: 30,
    daysToNextLevel: 12,
    levelUpdatedAt: Date.now(),
    isDefaultLevel: false,
    streakDates: [],
    availableMakeupCards: 2
  },
  makeup: {
    available: 2,
    used: 0,
    totalGranted: 3,
    maxAvailable: 12,
    monthlyGrant: 1,
    lastGrantedMonth: isoDate(0).slice(0, 7),
    currentMonthGranted: true,
    atLimit: false
  },
  makeupCandidates: [
    { date: shiftDate(-2), daysAgo: 2 },
    { date: shiftDate(-9), daysAgo: 9 },
    { date: shiftDate(-21), daysAgo: 21 }
  ],
  makeupHistory: [
    { id: 1, user_id: USER_ID, granted_month: isoDate(0).slice(0, 7), status: 'AVAILABLE', used_for_date: null, used_at: null, created_at: Date.now() - 86400000 },
    { id: 2, user_id: USER_ID, granted_month: shiftDate(-30).slice(0, 7), status: 'AVAILABLE', used_for_date: null, used_at: null, created_at: Date.now() - 30 * 86400000 }
  ],
  city: 'auto_ip'
}

const INTERACTION_ITEMS = [
  { id: 'coffee', name: '咖啡', icon: '☕', iconUrl: '', costPoints: 5, sortOrder: 10 },
  { id: 'noodles', name: '泡面', icon: '🍜', iconUrl: '', costPoints: 7, sortOrder: 20 },
  { id: 'gamepad', name: '手柄', icon: '🎮', iconUrl: '', costPoints: 13, sortOrder: 30 },
  { id: 'white_dragon', name: '白龙', icon: '🚬', iconUrl: '', costPoints: 16, sortOrder: 40 },
  { id: 'energy_bar', name: '能量棒', icon: '🍫', iconUrl: '', costPoints: 10, sortOrder: 50 }
]

const LEVEL_CONFIG = {
  levels: [
    { level_code: 'PANDA_LV1', level_name: '初生熊猫', threshold_days: 3, sort_order: 10 },
    { level_code: 'PANDA_LV2', level_name: '好奇宝宝', threshold_days: 7, sort_order: 20 },
    { level_code: 'PANDA_LV3', level_name: '竹林新秀', threshold_days: 15, sort_order: 30 },
    { level_code: 'PANDA_LV4', level_name: '黑白骑士', threshold_days: 30, sort_order: 40 },
    { level_code: 'PANDA_LV5', level_name: '功夫大师', threshold_days: 60, sort_order: 50 },
    { level_code: 'PANDA_LV6', level_name: '熊猫长老', threshold_days: 100, sort_order: 60 },
    { level_code: 'PANDA_LV7', level_name: '传奇熊猫', threshold_days: 200, sort_order: 70 }
  ],
  defaultLevelCode: 'NONE',
  defaultLevelName: '',
  makeupCardMax: 12,
  breakGapDays: 5,
  warningGapDays: [3, 4]
}

/* -------------------------------------------------------------------------- */
/* 工具                                                                        */
/* -------------------------------------------------------------------------- */

function isoDate(offsetDays) {
  const d = new Date(Date.now() + offsetDays * 86400000)
  return d.toISOString().slice(0, 10)
}
function shiftDate(days) {
  return isoDate(days)
}
function nowMs() {
  return Date.now()
}

/** 业务时区（默认 UTC+8）的「今日」字符串。 */
function businessToday() {
  const offset = Number(process.env.BIZ_TZ_OFFSET_HOURS ?? 8)
  return new Date(Date.now() + offset * 3600000).toISOString().slice(0, 10)
}

function envelope(data, traceId = `trace_${randomUUID()}`, code = 0, message = 'ok') {
  return { code, message, data, traceId }
}

function json(res, status, body) {
  const payload = JSON.stringify(body)
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(payload)
  })
  res.end(payload)
}

/** `http_api` 风格的鉴权失败：`{"detail":{"code":40101,...}}`（§5.2 形状陷阱）。 */
function authFail(res, code = 40101, message = '鉴权失败') {
  json(res, 401, { detail: { code, message, data: null, traceId: `trace_${randomUUID()}` } })
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = []
    req.on('data', (c) => chunks.push(c))
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8')
      if (!raw) return resolve({})
      try {
        resolve(JSON.parse(raw))
      } catch {
        resolve({})
      }
    })
  })
}

function issueTokens(deviceId) {
  const accessToken = `acc_${randomUUID()}`
  const refreshToken = `ref_${randomUUID()}`
  state.accessTokens.set(accessToken, { userId: USER_ID, deviceId })
  state.refreshTokens.set(refreshToken, { userId: USER_ID, deviceId })
  return {
    accessToken,
    refreshToken,
    tokenType: 'Bearer',
    expiresIn: ACCESS_TTL_SEC,
    userId: USER_ID,
    deviceId
  }
}

function authOf(req) {
  const header = req.headers.authorization ?? ''
  const token = header.startsWith('Bearer ') ? header.slice(7) : ''
  return state.accessTokens.get(token) ?? null
}

function log(...args) {
  console.log(`[mock ${new Date().toISOString().slice(11, 19)}]`, ...args)
}

/* -------------------------------------------------------------------------- */
/* 积分账本                                                                    */
/* -------------------------------------------------------------------------- */

function addLedger(reasonCode, changeAmount, relatedItemId = null, businessDate = businessToday()) {
  state.balance += changeAmount
  const entry = {
    id: state.ledgerSeq++,
    user_id: USER_ID,
    change_amount: changeAmount,
    reason_code: reasonCode,
    balance_after: state.balance,
    related_item_id: relatedItemId,
    idempotency_key: `${reasonCode}:${USER_ID}:${state.ledgerSeq}`,
    created_at: nowMs(),
    business_date: businessDate
  }
  state.ledger.unshift(entry)
  return entry
}

function broadcast(payload) {
  const sockets = state.connections.get(USER_ID)
  if (!sockets || sockets.size === 0) return 0
  const raw = JSON.stringify(payload)
  let sent = 0
  for (const ws of sockets) {
    if (ws.readyState === 1) {
      ws.send(raw)
      sent += 1
    }
  }
  return sent
}

function pushPointsChanged(entry) {
  broadcast({
    type: 'points.changed',
    payload: {
      ledgerId: entry.id,
      reasonCode: entry.reason_code,
      changeAmount: entry.change_amount,
      balanceAfter: entry.balance_after,
      balance: state.balance,
      relatedItemId: entry.related_item_id,
      businessDate: entry.business_date,
      timestamp: nowMs()
    }
  })
}

/* -------------------------------------------------------------------------- */
/* WS：回复生成（多气泡 + 流式 + TIMER）                                        */
/* -------------------------------------------------------------------------- */

/** 生成回复文本：**含换行**，用于验证客户端的多气泡拆分（FR-CHAT-6）。 */
function composeReply(text, itemName) {
  if (itemName) {
    return `啧……${itemName}啊。\n行吧，我收下了。\n${text ? `你刚说「${text}」，我记着了。` : '别以为这样就能让我态度好一点。'}`
  }
  if (!text) return '……'
  if (/晚安|睡了/.test(text)) return '哦。\n那就去睡啊。\n别硬撑着。'
  if (/累|困/.test(text)) return '累了就歇会儿。\n代码不会跑。'
  return `你说「${text}」。\n嗯，我听着呢。\n继续。`
}

/**
 * 模拟服务端的防抖 + 流式生成。
 * @param {string[]} requestIds 参与本轮回复的用户 requestId（含被合并的）
 * @param {Array<{requestId: string, content: string|null}>|null} items 逐条消息内容
 */
async function runReplyFlow({ requestIds, mergedText, itemName, withTimer = false, items = null }) {
  const mergedRequestId = requestIds[0]

  broadcast({ type: 'chat.typing', payload: { typing: true, stage: itemName ? 'generating' : 'generating' } })
  await sleep(120)

  const reply = composeReply(mergedText, itemName)
  const messageId = randomUUID()

  // 逐字符流式推送
  for (const ch of reply) {
    broadcast({
      type: 'chat.reply.stream',
      requestId: mergedRequestId,
      payload: { delta: ch, done: false, contentType: 'text', modelProvider: 'deepseek' }
    })
    await sleep(8)
  }

  const timestamp = nowMs()
  /*
   * 落服务端时间线（用户 + Bot）。
   *
   * ⚠️ 契约必须与生产 ws_api 一致：被合并的**每一条**用户消息都要有自己的一行，
   *    且 `messageId === 客户端 requestId`、`content` 是该条自己的文本。
   *    旧 mock 只写一行 `u_{mergedRequestId}`：
   *      - `u_` 前缀让客户端永远对不齐本地行（线上没有这个前缀）；
   *      - 只写一行让其余被合并消息永远无法被历史同步修复成 sent。
   *    mock 一旦与生产不一致，客户端自检会「全绿但线上照坏」——本文件的口径即此。
   */
  const userRows = items && items.length ? items : requestIds.map((id) => ({ requestId: id, content: null }))
  userRows.forEach((item, index) => {
    state.history.push({
      messageId: item.requestId,
      userId: USER_ID,
      role: 'user',
      content: item.content != null ? item.content : index === userRows.length - 1 ? (mergedText ?? '') : '',
      timestamp: timestamp - userRows.length + index
    })
  })
  state.history.push({ messageId, userId: USER_ID, role: 'bot', content: reply, timestamp })

  broadcast({
    type: 'chat.reply.stream',
    requestId: mergedRequestId,
    payload: {
      delta: '',
      done: true,
      messageId,
      finalContent: reply,
      timestamp,
      contentType: 'text',
      modelProvider: 'deepseek',
      timerInstruction: withTimer ? { target: '02:00', text: '两点了，快去睡！' } : null,
      requestIds
    }
  })
  broadcast({ type: 'chat.typing', payload: { typing: false } })

  // 积分：每日首次对话（幂等：这里只发一次以免测试噪声）
  const entry = addLedger('DAILY_FIRST_CHAT', 1)
  pushPointsChanged(entry)

  log('已回复', { requestId: mergedRequestId, messageId, requestIds, chars: reply.length })

  // 记忆提炼（异步）
  setTimeout(() => {
    const fact = { factId: randomUUID(), userId: USER_ID, fact: `用户提到：${(mergedText ?? '').slice(0, 24)}`, timestamp: nowMs() }
    state.facts.unshift(fact)
    broadcast({ type: 'memory.fact.created', payload: fact })
  }, 200)

  return { messageId, reply }
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms))
}

/* -------------------------------------------------------------------------- */
/* HTTP 路由                                                                   */
/* -------------------------------------------------------------------------- */

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`)
  const path = url.pathname
  const method = req.method ?? 'GET'

  log(`${method} ${path}${url.search}`)

  /* ---------------------------- healthz（只保证 200） ------------------- */
  if (path === '/healthz') {
    return json(res, 200, { status: 'ok', service: 'ws_api' })
  }
  if (path === '/api/v1/healthz') {
    return json(res, 200, envelope({ status: 'up' }))
  }

  /* --------------------------------- 鉴权 ------------------------------ */
  if (path === '/api/v1/auth/login' && method === 'POST') {
    const body = await readBody(req)
    if (!body.username || !body.password) {
      // 无信封，但 FastAPI 会包 detail —— 真实后端此处为 HTTPException(400, detail={...})
      return json(res, 400, { detail: { code: 40001, message: 'username/password required' } })
    }
    if (body.username !== USER || body.password !== PASS) {
      return json(res, 401, { detail: { code: 40101, message: 'invalid credentials' } })
    }
    const deviceId = body.deviceId || `device_${randomUUID().replace(/-/g, '')}`
    if (DEVICE_WHITELIST.length > 0 && !DEVICE_WHITELIST.includes(deviceId)) {
      return json(res, 403, { detail: { code: 40301, message: 'device not allowed' } })
    }
    if (KICK_ON_LOGIN) {
      // 模拟 AUTH_MAX_DEVICES 为正数时的「静默顶号」：清掉所有旧 refresh token
      state.refreshTokens.clear()
    }
    const tokens = issueTokens(deviceId)
    log('登录成功', { deviceId, userId: USER_ID })
    return json(res, 200, tokens)
  }

  if (path === '/api/v1/auth/refresh' && method === 'POST') {
    const body = await readBody(req)
    const record = state.refreshTokens.get(body.refreshToken)
    if (!record) {
      return json(res, 401, { detail: { code: 40102, message: 'refresh token invalid' } })
    }
    // Refresh Token 轮转：旧 **refresh** token 立即失效（与真实后端一致）
    state.refreshTokens.delete(body.refreshToken)
    const tokens = issueTokens(record.deviceId)
    // NOTE: 真实后端的 access token 是**自包含 JWT**，服务端不保存、也不在轮转时作废，
    //       而是在各自 TTL 到期前一直有效（`auth_utils.create_access_token` + 签名校验）。
    //       因此这里**故意不删除**旧 access token —— 否则会模拟出一个真实后端不存在的
    //       「旧 access token 突然失效」竞态，让客户端的并发续签看起来失败。
    log('Token 轮转成功', { deviceId: record.deviceId })
    return json(res, 200, tokens)
  }

  /* ------------------------ 以下接口需要鉴权 --------------------------- */
  const auth = authOf(req)
  if (!auth) {
    return authFail(res)
  }

  /* ------------------------------- 聊天历史 ---------------------------- */
  if (path === '/api/v1/chat/history' && method === 'GET') {
    const since = Number(url.searchParams.get('since') ?? 0)
    const limit = Math.max(1, Math.min(Number(url.searchParams.get('limit') ?? 200), 500))
    const items = state.history.filter((x) => x.timestamp > since && x.userId === auth.userId).slice(-limit)
    return json(res, 200, envelope({ items }))
  }

  if (path === '/api/v1/memory/facts' && method === 'GET') {
    const since = Number(url.searchParams.get('since') ?? 0)
    const limit = Math.max(1, Math.min(Number(url.searchParams.get('limit') ?? 200), 500))
    const items = state.facts.filter((x) => x.timestamp > since).slice(-limit)
    return json(res, 200, envelope({ items }))
  }

  /* ------------------------------ 城市设置 ----------------------------- */
  if (path === '/api/v1/settings/city' && method === 'GET') {
    return json(res, 200, envelope({ city: state.city }))
  }
  if (path === '/api/v1/settings/city' && method === 'PUT') {
    const body = await readBody(req)
    const city = (body.city ?? '').trim()
    if (!city) return json(res, 400, envelope(null, undefined, 40002, 'city 不能为空'))
    state.city = city
    return json(res, 200, envelope({ city }))
  }

  /* ---------------------------- REST 降级通道 -------------------------- */
  if (path === '/api/v1/chat' && method === 'POST') {
    const body = await readBody(req)
    if (!body.message?.trim()) return json(res, 400, envelope(null, undefined, 40001, 'message 不能为空'))
    const reply = composeReply(body.message.trim())
    const messageId = randomUUID()
    /*
     * ⚠️ 契约必须与生产 http_api 一致：
     *   1. 用户行 messageId == 客户端 requestId（裸 id，不加前缀）。
     *      客户端本地那条乐观消息的主键就是 requestId，只有相等才能被
     *      GET /chat/history 就地修成 sent。写成 `u_{requestId}` 会让客户端
     *      永远修不好、还会多出一条重复气泡 —— 而这正是本次故障的形态，
     *      mock 里加前缀会把这个缺陷**藏起来**（客户端自检全绿但线上照坏）。
     *   2. 响应回传 `messageId`，客户端用它做 bot 气泡主键，避免同步时重复插入。
     */
    state.history.push({ messageId: body.requestId ?? randomUUID(), userId: USER_ID, role: 'user', content: body.message.trim(), timestamp: nowMs() - 1 })
    state.history.push({ messageId, userId: USER_ID, role: 'bot', content: reply, timestamp: nowMs() })
    return json(res, 200, envelope({ conversationId: 'default_session', reply, messageId, model: 'deepseek', debounceWindowSec: 8 }))
  }

  /* ------------------------------ 互动礼物 ----------------------------- */
  if (path === '/api/v1/interaction/items' && method === 'GET') {
    const items = INTERACTION_ITEMS.map((it) => ({ ...it, affordable: state.balance >= it.costPoints }))
    return json(res, 200, envelope({ balance: state.balance, dailyLimit: null, items }))
  }

  if (path === '/api/v1/interaction/send' && method === 'POST') {
    const body = await readBody(req)
    const item = INTERACTION_ITEMS.find((x) => x.id === body.itemId)
    if (!body.itemId) {
      return json(res, 200, envelope(null, undefined, 40001, '缺少 itemId'))
    }
    if (!item) {
      return json(res, 200, envelope({ balance: state.balance, charged: 0 }, undefined, 40202, '物品不存在或已下架'))
    }
    if (state.balance < item.costPoints) {
      return json(
        res,
        200,
        envelope({ balance: state.balance, charged: 0, refunded: false }, undefined, 40201, '积分不足')
      )
    }

    const text = (body.text ?? '').trim()
    let mergedRequestIds = []

    // §7.1a：无附言且有缓冲中的用户消息 → 等待静默窗口后「摘走」
    let takenItems = []
    if (!text) {
      const buffer = state.messageBuffer.get(auth.userId) ?? []
      if (buffer.length > 0) {
        broadcast({ type: 'chat.typing', payload: { typing: true, stage: 'interaction_merge' } })
        await sleep(MERGE_WAIT_MS)
        const taken = state.messageBuffer.get(auth.userId) ?? []
        state.messageBuffer.set(auth.userId, [])
        takenItems = taken.map((m) => ({ requestId: m.requestId, content: m.content }))
        mergedRequestIds = taken.map((m) => m.requestId)
        const mergedText = taken.map((m) => m.content).filter(Boolean).join('\n')
        log('摘走防抖缓冲', { count: taken.length, mergedRequestIds })
        void mergedText
      }
    }

    // 扣分（幂等：同 requestId 只扣一次）
    const idempotencyKey = `item_send:${USER_ID}:${body.requestId}`
    const existing = state.ledger.find((e) => e.idempotency_key === idempotencyKey)
    const duplicate = !!existing
    if (!duplicate) {
      const entry = addLedger('ITEM_SEND', -item.costPoints, item.id)
      entry.idempotency_key = idempotencyKey
      pushPointsChanged(entry)
    }

    const fallback = FAIL_INTERACTION
    if (fallback) {
      // 40204：AI 最终失败 → 已退款
      const refund = addLedger('ITEM_REFUND', item.costPoints, item.id)
      pushPointsChanged(refund)
      const fallbackText = '……刚刚走神了。'
      await runReplyFlow({
        requestIds: [body.requestId, ...mergedRequestIds],
        mergedText: text,
        itemName: item.name,
        items: [{ requestId: body.requestId, content: text ?? '' }, ...takenItems]
      })
      broadcast({
        type: 'chat.reply.stream',
        requestId: body.requestId,
        payload: {
          delta: '',
          done: true,
          messageId: randomUUID(),
          finalContent: fallbackText,
          timestamp: nowMs(),
          contentType: 'text',
          modelProvider: 'deepseek',
          messageKind: 'interaction_failed',
          interactionItemId: item.id,
          interactionItemName: item.name,
          interactionItemIcon: item.icon,
          interactionFailed: true,
          requestIds: [body.requestId]
        }
      })
      return json(
        res,
        200,
        envelope(
          {
            success: false,
            itemId: item.id,
            requestId: body.requestId,
            balance: state.balance,
            charged: item.costPoints,
            refunded: true,
            duplicate,
            item: { id: item.id, name: item.name, icon: item.icon, costPoints: item.costPoints },
            fallbackText,
            mergedCount: mergedRequestIds.length,
            mergedRequestIds
          },
          undefined,
          40204,
          'AI 调用最终失败（已退款）'
        )
      )
    }

    // 交互回复：先推 typing(stage=interaction)，再走既有 chat.reply.stream 通道
    broadcast({ type: 'chat.typing', payload: { typing: true, stage: 'interaction' } })
    await sleep(80)
    const reply = composeReply(text, item.name)
    const messageId = randomUUID()
    for (const ch of reply) {
      broadcast({
        type: 'chat.reply.stream',
        requestId: body.requestId,
        payload: {
          delta: ch,
          done: false,
          contentType: 'text',
          modelProvider: 'deepseek',
          messageKind: 'interaction'
        }
      })
      await sleep(8)
    }
    const timestamp = nowMs()
    /*
     * 落服务端时间线（用户 + Bot）。契约同生产 `InteractionHandler`：
     *   · 被摘走的聊天消息逐条写行（messageId == 客户端 requestId）；
     *   · 礼物本身写一行 interaction_user_{requestId}；
     *   · 最后是 Bot 回复行。
     * 旧 mock 只写 Bot 行：于是「live 帧丢失后被合并消息永久修不回来」
     * 这个线上缺陷在客户端自检里完全看不见（自检全绿、线上照坏）。
     */
    takenItems.forEach((taken, index) => {
      state.history.push({
        messageId: taken.requestId,
        userId: USER_ID,
        role: 'user',
        content: taken.content ?? '',
        timestamp: timestamp - takenItems.length + index - 1
      })
    })
    state.history.push({
      messageId: `interaction_user_${body.requestId}`,
      userId: USER_ID,
      role: 'user',
      content: `${item.icon} ${item.name}`.trim(),
      timestamp: timestamp - 1
    })
    state.history.push({ messageId, userId: USER_ID, role: 'bot', content: reply, timestamp })
    broadcast({
      type: 'chat.reply.stream',
      requestId: body.requestId,
      payload: {
        delta: '',
        done: true,
        messageId,
        finalContent: reply,
        timestamp,
        contentType: 'text',
        modelProvider: 'deepseek',
        messageKind: 'interaction',
        interactionItemId: item.id,
        interactionItemName: item.name,
        interactionItemIcon: item.icon,
        interactionFailed: false,
        requestIds: [body.requestId, ...mergedRequestIds],
        timerInstruction: item.id === 'gamepad' ? { target: '02:00', text: '两点了，该睡了' } : null
      }
    })
    broadcast({ type: 'chat.typing', payload: { typing: false } })

    log('互动发送成功', { itemId: item.id, balance: state.balance, merged: mergedRequestIds.length })

    return json(
      res,
      200,
      envelope({
        success: true,
        itemId: item.id,
        requestId: body.requestId,
        balance: state.balance,
        charged: item.costPoints,
        refunded: false,
        duplicate,
        item: { id: item.id, name: item.name, icon: item.icon, costPoints: item.costPoints },
        reply,
        messageId,
        timerInstruction: null,
        mergedCount: mergedRequestIds.length,
        mergedRequestIds
      })
    )
  }

  /* -------------------------------- 积分 ------------------------------- */
  if (path === '/api/v1/points/balance' && method === 'GET') {
    return json(res, 200, envelope({ balance: state.balance, updatedAt: nowMs() }))
  }

  if (path === '/api/v1/points/overview' && method === 'GET') {
    return json(
      res,
      200,
      envelope({
        balance: state.balance,
        balanceUpdatedAt: nowMs(),
        level: state.level,
        makeupCard: state.makeup
      })
    )
  }

  if (path === '/api/v1/points/history' && method === 'GET') {
    const page = Math.max(1, Number(url.searchParams.get('page') ?? 1))
    const pageSize = Math.max(1, Math.min(Number(url.searchParams.get('pageSize') ?? 20), 100))
    const reasonCode = url.searchParams.get('reasonCode')
    const filtered = reasonCode ? state.ledger.filter((e) => e.reason_code === reasonCode) : state.ledger
    const start = (page - 1) * pageSize
    const items = filtered.slice(start, start + pageSize)
    return json(
      res,
      200,
      envelope({
        items,
        total: filtered.length,
        page,
        pageSize,
        hasMore: start + items.length < filtered.length
      })
    )
  }

  /* -------------------------------- 等级 ------------------------------- */
  if (path === '/api/v1/level/status' && method === 'GET') {
    return json(res, 200, envelope({ ...state.level, availableMakeupCards: state.makeup.available }))
  }

  if (path === '/api/v1/level/config' && method === 'GET') {
    // ⚠️ levels[] 为 snake_case（EDGE-L24）
    return json(res, 200, envelope(LEVEL_CONFIG))
  }

  /* ------------------------------ 补签卡 ------------------------------- */
  if (path === '/api/v1/points/makeup-card' && method === 'GET') {
    return json(res, 200, envelope(state.makeup))
  }

  if (path === '/api/v1/points/makeup-card/candidates' && method === 'GET') {
    const limit = Math.max(1, Number(url.searchParams.get('limit') ?? 120))
    const items = state.makeupCandidates.slice(0, limit)
    return json(
      res,
      200,
      envelope({ items, total: state.makeupCandidates.length, firstActivityDate: shiftDate(-30), available: state.makeup.available })
    )
  }

  if (path === '/api/v1/points/makeup-card/use' && method === 'POST') {
    const body = await readBody(req)
    const targetDate = (body.targetDate ?? '').trim()
    if (!targetDate) {
      return json(res, 200, envelope({ success: false }, undefined, 40001, '缺少 targetDate'))
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(targetDate) || targetDate > businessToday()) {
      return json(res, 200, envelope({ success: false, availableCards: state.makeup.available }, undefined, 40207, '目标日期非法'))
    }
    if (state.makeup.available <= 0) {
      return json(res, 200, envelope({ success: false, availableCards: 0 }, undefined, 40205, '补签卡不足'))
    }
    const idx = state.makeupCandidates.findIndex((c) => c.date === targetDate)
    if (idx < 0) {
      // 已有有效对话记录（不在候选缺口里）
      return json(res, 200, envelope({ success: false, availableCards: state.makeup.available }, undefined, 40206, '该日期已有有效对话记录'))
    }

    state.makeupCandidates.splice(idx, 1)
    state.makeup.available -= 1
    state.makeup.used += 1
    const card = {
      id: state.makeupHistory.length + 1,
      user_id: USER_ID,
      granted_month: state.makeup.lastGrantedMonth,
      status: 'USED',
      used_for_date: targetDate,
      used_at: nowMs(),
      created_at: nowMs()
    }
    state.makeupHistory.unshift(card)

    // 等级恢复（FR-MC-5：`changeType=RESTORE` 走克制反馈）
    state.level = {
      ...state.level,
      changeType: 'RESTORE',
      changeSource: 'MAKEUP_CARD',
      continuousDays: state.level.continuousDays + 1,
      gapDays: 0,
      daysToNextLevel: Math.max(0, (state.level.nextLevelThresholdDays ?? 30) - (state.level.continuousDays + 1))
    }

    broadcast({
      type: 'makeup_card.changed',
      payload: {
        reason: 'USED',
        available: state.makeup.available,
        used: state.makeup.used,
        totalGranted: state.makeup.totalGranted,
        maxAvailable: state.makeup.maxAvailable,
        lastGrantedMonth: state.makeup.lastGrantedMonth,
        timestamp: nowMs()
      }
    })
    broadcast({
      type: 'level.changed',
      payload: { ...state.level, prevLevelCode: state.level.prevLevelCode, timestamp: nowMs() }
    })

    log('补签成功', { targetDate, available: state.makeup.available })
    return json(
      res,
      200,
      envelope({
        success: true,
        availableCards: state.makeup.available,
        targetDate,
        card,
        level: { ...state.level, availableMakeupCards: state.makeup.available }
      })
    )
  }

  if (path === '/api/v1/points/makeup-card/history' && method === 'GET') {
    const page = Math.max(1, Number(url.searchParams.get('page') ?? 1))
    const pageSize = Math.max(1, Number(url.searchParams.get('pageSize') ?? 20))
    const start = (page - 1) * pageSize
    const items = state.makeupHistory.slice(start, start + pageSize)
    return json(
      res,
      200,
      envelope({ items, total: state.makeupHistory.length, page, pageSize, hasMore: start + items.length < state.makeupHistory.length })
    )
  }

  /* ------------------------- 测试辅助（非 PRD 接口） ------------------- */
  if (path === '/__test__/trigger' && method === 'POST') {
    const body = await readBody(req)
    const kind = body.kind
    if (kind === 'level_upgrade') {
      state.level = { ...state.level, levelCode: 'PANDA_LV4', levelName: '黑白骑士', changeType: 'UPGRADE', continuousDays: 30, prevLevelCode: 'PANDA_LV3' }
      broadcast({ type: 'level.changed', payload: { ...state.level, timestamp: nowMs() } })
    } else if (kind === 'streak_warning') {
      broadcast({
        type: 'streak.warning',
        payload: {
          levelCode: state.level.levelCode,
          levelName: state.level.levelName,
          continuousDays: state.level.continuousDays,
          gapDays: 3,
          remainingDays: 2,
          deadlineDate: isoDate(2),
          timestamp: nowMs()
        }
      })
    } else if (kind === 'makeup_grant') {
      state.makeup.available += 1
      state.makeup.totalGranted += 1
      broadcast({
        type: 'makeup_card.changed',
        payload: { reason: 'MONTHLY_GRANT', ...state.makeup, timestamp: nowMs() }
      })
    } else if (kind === 'bot_error') {
      broadcast({
        type: 'bot.error',
        requestId: body.requestId ?? randomUUID(),
        payload: { errorCode: 'AI_TIMEOUT', message: 'AI 响应超时，请重试', requestIds: [], timestamp: nowMs() }
      })
    } else if (kind === 'points_snapshot') {
      broadcast({ type: 'points.snapshot', payload: { userId: USER_ID, balance: state.balance, timestamp: nowMs() } })
    } else if (kind === 'greeting') {
      const greetingId = randomUUID()
      broadcast({
        type: 'chat.reply.stream',
        requestId: greetingId,
        payload: {
          delta: '',
          done: true,
          messageId: greetingId,
          finalContent: '……醒了？\n早。',
          timestamp: nowMs(),
          contentType: 'text',
          modelProvider: 'deepseek',
          requestIds: [greetingId],
          messageKind: 'greeting',
          greetingScenario: 'morning'
        }
      })
    } else if (kind === 'unknown_frame') {
      // NFR-12：客户端必须忽略未知 type，不得抛错
      broadcast({ type: 'future.feature.event', payload: { whatever: true }, extraTopLevel: 1 })
    } else if (kind === 'seed_history') {
      const n = Number(body.count ?? 12)
      for (let i = n; i > 0; i -= 1) {
        const ts = nowMs() - i * 60000
        state.history.push({ messageId: `seed_u_${i}`, userId: USER_ID, role: 'user', content: `历史用户消息 ${i}`, timestamp: ts })
        state.history.push({
          messageId: `seed_b_${i}`,
          userId: USER_ID,
          role: 'bot',
          content: `历史回复 ${i} 第一行\n历史回复 ${i} 第二行`,
          timestamp: ts + 1
        })
      }
      broadcast({ type: 'points.snapshot', payload: { userId: USER_ID, balance: state.balance, timestamp: nowMs() } })
    } else {
      return json(res, 400, envelope(null, undefined, 40001, `unknown kind: ${kind}`))
    }
    return json(res, 200, envelope({ triggered: kind }))
  }

  if (path === '/__test__/state' && method === 'GET') {
    return json(res, 200, {
      balance: state.balance,
      ledgerCount: state.ledger.length,
      historyCount: state.history.length,
      factCount: state.facts.length,
      makeup: state.makeup,
      candidates: state.makeupCandidates,
      level: state.level,
      connections: state.connections.get(USER_ID)?.size ?? 0,
      accessTokens: state.accessTokens.size,
      refreshTokens: state.refreshTokens.size
    })
  }

  return json(res, 404, { detail: { code: 40400, message: `no route: ${method} ${path}` } })
})

/* -------------------------------------------------------------------------- */
/* WebSocket                                                                    */
/* -------------------------------------------------------------------------- */

const wss = new WebSocketServer({ noServer: true })

server.on('upgrade', (req, socket, head) => {
  const url = new URL(req.url, `http://${req.headers.host}`)
  if (url.pathname !== '/ws/chat') {
    socket.write('HTTP/1.1 404 Not Found\r\n\r\n')
    socket.destroy()
    return
  }

  // 三级回退的第 1 级：请求头 Authorization（Linux 端采用）
  const header = req.headers.authorization ?? ''
  const token = header.startsWith('Bearer ') ? header.slice(7) : ''
  const auth = state.accessTokens.get(token)

  // 第 3 级（query token）被真实后端明确拒绝
  if (!auth && url.searchParams.get('token')) {
    log('拒绝 query token 连接（与真实后端一致）')
  }

  wss.handleUpgrade(req, socket, head, (ws) => {
    ws.__auth = auth ?? null
    wss.emit('connection', ws, req)
  })
})

wss.on('connection', (ws) => {
  const auth = ws.__auth

  if (!auth) {
    // 先 accept 再下发 auth.expired，随后以 4001 关闭（§5.3）
    log('WS 鉴权失败：下发 auth.expired 并以 4001 关闭')
    ws.send(JSON.stringify({ type: 'auth.expired', payload: { reason: 'invalid_token', timestamp: nowMs() } }))
    setTimeout(() => ws.close(4001, 'Invalid token'), 30)
    return
  }

  const set = state.connections.get(auth.userId) ?? new Set()
  set.add(ws)
  state.connections.set(auth.userId, set)
  log('WS 已连接', { userId: auth.userId, deviceId: auth.deviceId, count: set.size })

  // 连接建立时下发余额快照
  ws.send(JSON.stringify({ type: 'points.snapshot', payload: { userId: auth.userId, balance: state.balance, timestamp: nowMs() } }))

  ws.on('message', async (raw) => {
    let frame
    try {
      frame = JSON.parse(raw.toString('utf8'))
    } catch {
      ws.send(JSON.stringify({ type: 'bot.error', requestId: null, payload: { errorCode: 'INVALID_JSON', message: '无效的 JSON 格式', requestIds: [], timestamp: nowMs() } }))
      return
    }

    // ⚠️ requestId 读**顶层**字段（与真实后端一致）
    const requestId = frame.requestId || randomUUID()
    const type = frame.type

    if (type === 'ping') {
      // pong 是顶层 {type,timestamp}，无 payload，且向该账号所有连接广播
      broadcast({ type: 'pong', timestamp: nowMs() })
      return
    }

    if (type === 'chat.message') {
      const payload = frame.payload ?? {}
      const content = (payload.content ?? '').trim()
      const images = payload.images ?? []

      if (!content && images.length === 0) {
        ws.send(JSON.stringify({ type: 'bot.error', requestId, payload: { errorCode: 'EMPTY_MESSAGE', message: '消息内容不能为空', requestIds: [requestId], timestamp: nowMs() } }))
        return
      }
      if (images.length > 3) {
        ws.send(JSON.stringify({ type: 'bot.error', requestId, payload: { errorCode: 'VISION_IMAGE_COUNT_EXCEEDED', message: '单次最多 3 张图片', requestIds: [requestId], timestamp: nowMs() } }))
        return
      }
      for (const [idx, img] of images.entries()) {
        const mime = (img.mimeType ?? '').toLowerCase()
        if (mime !== 'image/jpeg' && mime !== 'image/png') {
          ws.send(JSON.stringify({ type: 'bot.error', requestId, payload: { errorCode: 'VISION_INVALID_MIME', message: `第 ${idx + 1} 张图片格式不支持`, requestIds: [requestId], timestamp: nowMs() } }))
          return
        }
        const bytes = Buffer.from(img.dataBase64 ?? '', 'base64').length
        if (bytes > 20 * 1024 * 1024) {
          ws.send(JSON.stringify({ type: 'bot.error', requestId, payload: { errorCode: 'VISION_IMAGE_TOO_LARGE', message: `第 ${idx + 1} 张图片超过 20MB`, requestIds: [requestId], timestamp: nowMs() } }))
          return
        }
      }

      // 入防抖缓冲
      const buffer = state.messageBuffer.get(auth.userId) ?? []
      buffer.push({ requestId, content })
      state.messageBuffer.set(auth.userId, buffer)

      // 多设备回声（自己也会收到，客户端需按 requestId 幂等去重）
      broadcast({
        type: 'chat.message.echo',
        requestId,
        payload: { content, imageCount: images.length, timestamp: nowMs(), originDeviceId: auth.deviceId }
      })
      // EDGE-L17：debounceWindowSec 由服务端下发
      broadcast({ type: 'chat.queued', requestId, payload: { debounceWindowSec: 8 } })

      // 防抖窗口（测试期缩短为 400ms）
      const debounceMs = Number(process.env.DEBOUNCE_MS ?? 400)
      setTimeout(() => {
        const pending = state.messageBuffer.get(auth.userId) ?? []
        if (pending.length === 0) return
        state.messageBuffer.set(auth.userId, [])
        const requestIds = pending.map((m) => m.requestId)
        const mergedText = pending.map((m) => m.content).filter(Boolean).join('\n')
        // 图片走 vision 两段式提示（FR-IMG-10）
        if (images.length > 0) {
          broadcast({ type: 'chat.typing', payload: { typing: true, stage: 'vision' } })
          setTimeout(() => {
            void runReplyFlow({ requestIds, mergedText, itemName: null, items: pending, withTimer: /两点|叫醒|闹钟/.test(mergedText) })
          }, 200)
        } else {
          void runReplyFlow({ requestIds, mergedText, itemName: null, items: pending, withTimer: /两点|叫醒|闹钟/.test(mergedText) })
        }
      }, debounceMs)
      return
    }

    ws.send(JSON.stringify({ type: 'bot.error', requestId, payload: { errorCode: 'UNKNOWN_TYPE', message: `未知的消息类型: ${type}`, requestIds: [], timestamp: nowMs() } }))
  })

  ws.on('close', () => {
    set.delete(ws)
    log('WS 已断开', { userId: auth.userId, remaining: set.size })
  })

  ws.on('error', (err) => log('WS 错误', err.message))
})

server.listen(PORT, HOST, () => {
  const base = `http://${HOST}:${PORT}`
  log('='.repeat(66))
  log(`Mock 后端已启动  REST=${base}/api/v1/  WS=ws://${HOST}:${PORT}/ws/chat`)
  log(`登录账号：${USER} / ${PASS}   设备白名单：${DEVICE_WHITELIST.length ? DEVICE_WHITELIST.join(',') : '(未启用)'}`)
  log(`合并等待 MERGE_WAIT_MS=${MERGE_WAIT_MS}  互动失败模拟 FAIL_INTERACTION=${FAIL_INTERACTION ? 'on' : 'off'}`)
  log(`测试辅助：POST ${base}/__test__/trigger  {"kind":"..."}   GET ${base}/__test__/state`)
  log('='.repeat(66))
})

process.on('SIGINT', () => {
  log('正在关闭…')
  wss.close()
  server.close(() => process.exit(0))
})
