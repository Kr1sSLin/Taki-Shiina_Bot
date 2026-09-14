#!/usr/bin/env node
/**
 * 协议一致性自检（§5 / §7.0 契约）。
 *
 * 用与客户端**完全相同**的握手与帧格式打 Mock 后端，逐条验证 PRD 里最容易踩坑的约定。
 * 这些断言随后也会用来核对真实后端（把 BASE 换成生产域名即可）。
 *
 * 用法：
 *   node mock-server/server.mjs &          # 先起 Mock
 *   node scripts/verify-contract.mjs       # 再跑自检
 *   BASE=https://takishiinabot.top node scripts/verify-contract.mjs   # 也可对真实后端跑（需账号）
 */

import WebSocket from 'ws'

const HOST = process.env.HOST ?? '127.0.0.1'
const PORT = Number(process.env.PORT ?? 8787)
const BASE = process.env.BASE ?? `http://${HOST}:${PORT}`
const WS_BASE = process.env.WS_BASE ?? BASE.replace(/^http/, 'ws')
const USERNAME = process.env.USERNAME_FOR_LOGIN ?? 'kris'
const PASSWORD = process.env.PASSWORD_FOR_LOGIN ?? 'taki'

let pass = 0
let fail = 0
const failures = []

function check(name, condition, detail) {
  if (condition) {
    pass += 1
    console.log(`  \x1b[32m✓\x1b[0m ${name}`)
  } else {
    fail += 1
    failures.push(name)
    console.log(`  \x1b[31m✗\x1b[0m ${name}${detail !== undefined ? `  →  ${JSON.stringify(detail)}` : ''}`)
  }
}

function section(title) {
  console.log(`\n\x1b[1m${title}\x1b[0m`)
}

async function req(path, { method = 'GET', body, token, timeoutMs = 90000 } = {}) {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const res = await fetch(`${BASE}${path}`, {
      method,
      headers: {
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...(token ? { authorization: `Bearer ${token}` } : {})
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: controller.signal
    })
    const text = await res.text()
    let json = null
    try {
      json = text ? JSON.parse(text) : null
    } catch {
      json = null
    }
    return { status: res.status, json, text }
  } finally {
    clearTimeout(timer)
  }
}

/** §5.2：错误码可能在顶层，也可能在 `detail.code`（http_api 的二次包装）。 */
function extractCode(body) {
  if (!body || typeof body !== 'object') return null
  if (typeof body.code === 'number') return body.code
  if (body.detail && typeof body.detail.code === 'number') return body.detail.code
  return null
}

/* -------------------------------------------------------------------------- */

async function main() {
  console.log(`\n\x1b[1mTKS 客户端协议一致性自检\x1b[0m   BASE=${BASE}  WS=${WS_BASE}/ws/chat\n${'─'.repeat(70)}`)

  /* ------------------------------- healthz ------------------------------ */
  section('§5.2 连通性自检（只判断 HTTP 200，不解析响应体）')
  const health = await req('/healthz')
  check('GET /healthz 返回 200', health.status === 200, health.status)

  /* --------------------------------- 登录 ------------------------------- */
  section('§5.2 鉴权（无信封）')
  const bad = await req('/api/v1/auth/login', { method: 'POST', body: { username: USERNAME, password: 'wrong-password' } })
  check('错误密码返回 401', bad.status === 401, bad.status)
  check('登录失败错误码可提取（40101）', extractCode(bad.json) === 40101, bad.json)

  const login = await req('/api/v1/auth/login', {
    method: 'POST',
    body: { username: USERNAME, password: PASSWORD, deviceId: 'device_verify_harness' }
  })
  check('登录成功返回 200', login.status === 200, login.status)
  check('登录响应**无信封**（顶层即 accessToken）', typeof login.json?.accessToken === 'string', Object.keys(login.json ?? {}))
  check('登录响应含 refreshToken / expiresIn / userId / deviceId',
    typeof login.json?.refreshToken === 'string' &&
      typeof login.json?.expiresIn === 'number' &&
      typeof login.json?.userId === 'string' &&
      typeof login.json?.deviceId === 'string',
    login.json
  )
  const token = login.json.accessToken

  const noAuth = await req('/api/v1/points/balance')
  check('缺失 Authorization 返回 401', noAuth.status === 401, noAuth.status)
  check('http_api 鉴权失败形状为 detail.code（§5.2 陷阱）', extractCode(noAuth.json) === 40101, noAuth.json)

  /* ------------------------------ 积分体系 REST ------------------------- */
  section('§7.0 积分体系 REST（业务失败 = HTTP 200 + code != 0）')

  const overview = await req('/api/v1/points/overview', { token })
  check('GET /points/overview 成功', overview.status === 200 && overview.json?.code === 0, overview.json?.code)
  const ov = overview.json?.data
  check('overview 含 balance / level / makeupCard', typeof ov?.balance === 'number' && !!ov?.level && !!ov?.makeupCard)
  check('level 为 camelCase（levelCode / nextLevelThresholdDays / daysToNextLevel）',
    typeof ov?.level?.levelCode === 'string' &&
      typeof ov?.level?.nextLevelThresholdDays === 'number' &&
      typeof ov?.level?.daysToNextLevel === 'number',
    { levelCode: ov?.level?.levelCode, nextLevelThresholdDays: ov?.level?.nextLevelThresholdDays }
  )
  check('makeupCard 含服务端下发的 monthlyGrant / maxAvailable（FR-MC-1 不得硬编码）',
    typeof ov?.makeupCard?.monthlyGrant === 'number' && typeof ov?.makeupCard?.maxAvailable === 'number',
    ov?.makeupCard
  )

  const levelConfig = await req('/api/v1/level/config', { token })
  const levels = levelConfig.json?.data?.levels ?? []
  check('GET /level/config 成功且 levels 非空', levels.length > 0, levels.length)
  check('levels[] 为 snake_case（level_code / level_name / threshold_days / sort_order）',
    levels.every((l) => 'level_code' in l && 'level_name' in l && 'threshold_days' in l && 'sort_order' in l),
    Object.keys(levels[0] ?? {})
  )
  check('EDGE-L24 防御：所有 level_code 非空', levels.every((l) => typeof l.level_code === 'string' && l.level_code.length > 0))

  const ledger = await req('/api/v1/points/history?page=1&pageSize=5', { token })
  const ledgerItems = ledger.json?.data?.items ?? []
  check('GET /points/history 分页字段完整（total/page/pageSize/hasMore）',
    typeof ledger.json?.data?.total === 'number' &&
      typeof ledger.json?.data?.page === 'number' &&
      typeof ledger.json?.data?.pageSize === 'number' &&
      typeof ledger.json?.data?.hasMore === 'boolean',
    Object.keys(ledger.json?.data ?? {})
  )
  if (ledgerItems.length > 0) {
    check('history items[] 为 snake_case（change_amount / reason_code / balance_after / business_date）',
      'change_amount' in ledgerItems[0] && 'reason_code' in ledgerItems[0] && 'balance_after' in ledgerItems[0] && 'business_date' in ledgerItems[0],
      Object.keys(ledgerItems[0])
    )
  } else {
    check('history items[] 为 snake_case（无数据，跳过）', true)
  }

  const items = await req('/api/v1/interaction/items', { token })
  const menu = items.json?.data?.items ?? []
  check('GET /interaction/items 返回 balance / dailyLimit / items[]',
    typeof items.json?.data?.balance === 'number' && 'dailyLimit' in (items.json?.data ?? {}) && Array.isArray(menu),
    Object.keys(items.json?.data ?? {})
  )
  check('FR-INT-10：dailyLimit 为 null（服务端不设每日上限）', items.json?.data?.dailyLimit === null, items.json?.data?.dailyLimit)
  check('FR-INT-4：每项含服务端算好的 affordable 字段', menu.every((it) => typeof it.affordable === 'boolean'), menu.map((i) => i.affordable))
  check('FR-INT-2：items 按 sortOrder 升序返回',
    menu.every((it, i) => i === 0 || (menu[i - 1].sortOrder ?? 0) <= (it.sortOrder ?? 0)),
    menu.map((i) => i.sortOrder)
  )
  check('FR-INT-9：icon 为 emoji 且 iconUrl 可为空串',
    menu.every((it) => typeof it.icon === 'string' && it.icon.length > 0 && typeof it.iconUrl === 'string'),
    menu.map((i) => ({ icon: i.icon, iconUrl: i.iconUrl }))
  )

  const candidates = await req('/api/v1/points/makeup-card/candidates?limit=120', { token })
  check('GET /makeup-card/candidates 返回 items[{date,daysAgo}] / total / firstActivityDate / available',
    Array.isArray(candidates.json?.data?.items) &&
      typeof candidates.json?.data?.total === 'number' &&
      'firstActivityDate' in (candidates.json?.data ?? {}) &&
      typeof candidates.json?.data?.available === 'number',
    Object.keys(candidates.json?.data ?? {})
  )
  check('candidates[].date 为 YYYY-MM-DD（FR-PROG-3 客户端不得本地推算）',
    (candidates.json?.data?.items ?? []).every((c) => /^\d{4}-\d{2}-\d{2}$/.test(c.date)),
    (candidates.json?.data?.items ?? []).map((c) => c.date)
  )

  const makeupHistory = await req('/api/v1/points/makeup-card/history?page=1&pageSize=20', { token })
  const cards = makeupHistory.json?.data?.items ?? []
  if (cards.length > 0) {
    check('补签卡记录为 snake_case（granted_month / used_for_date / status）',
      'granted_month' in cards[0] && 'used_for_date' in cards[0] && 'status' in cards[0],
      Object.keys(cards[0])
    )
  } else {
    check('补签卡记录为 snake_case（无数据，跳过）', true)
  }

  const badDate = await req('/api/v1/points/makeup-card/use', { method: 'POST', token, body: { targetDate: '2999-01-01' } })
  check('40207：非法日期返回 HTTP 200 + code=40207（不得用 response.ok 判断成败）',
    badDate.status === 200 && badDate.json?.code === 40207,
    { status: badDate.status, code: badDate.json?.code }
  )

  /* --------------------------------- WS -------------------------------- */
  section('§5.3 WebSocket 契约')

  // 未鉴权：应先收到 auth.expired，再以 4001 关闭
  await new Promise((resolve) => {
    const badWs = new WebSocket(`${WS_BASE}/ws/chat`, { headers: { Authorization: 'Bearer invalid-token' } })
    let sawExpired = false
    let closeCode = null
    const timer = setTimeout(() => {
      check('无效 token：收到 auth.expired 且以 4001 关闭', sawExpired && closeCode === 4001, { sawExpired, closeCode })
      try {
        badWs.terminate()
      } catch {
        /* ignore */
      }
      resolve()
    }, 4000)
    badWs.on('message', (raw) => {
      const frame = JSON.parse(raw.toString())
      if (frame.type === 'auth.expired') sawExpired = true
    })
    badWs.on('close', (code) => {
      closeCode = code
      clearTimeout(timer)
      check('无效 token：收到 auth.expired 且以 4001 关闭（4001 不是可重试的网络错误）', sawExpired && closeCode === 4001, { sawExpired, closeCode })
      resolve()
    })
    badWs.on('error', () => {
      /* 预期内 */
    })
  })

  // 正常连接
  const ws = new WebSocket(`${WS_BASE}/ws/chat`, { headers: { Authorization: `Bearer ${token}` } })
  /** 累积所有下行帧（不重置——否则会在 await 期间把已到达的帧丢掉）。 */
  const frames = []
  /** 取「自某个标记之后」到达的帧。 */
  const mark = () => frames.length
  const since = (m) => frames.slice(m)
  ws.on('message', (raw) => frames.push(JSON.parse(raw.toString())))

  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('WS 连接超时')), 6000)
    ws.on('open', () => {
      clearTimeout(timer)
      resolve()
    })
    ws.on('error', (err) => {
      clearTimeout(timer)
      reject(err)
    })
  })
  check('WS 使用请求头 Authorization 鉴权连接成功（§5.3 第 1 级）', ws.readyState === WebSocket.OPEN)

  // ping → pong（顶层，无 payload）
  ws.send(JSON.stringify({ type: 'ping', payload: { timestamp: Date.now() } }))
  await waitFor(() => frames.some((f) => f.type === 'pong'), 3000)
  const pong = frames.find((f) => f.type === 'pong')
  check('ping → 收到 pong', !!pong)
  check('pong 是顶层 {type,timestamp}，**没有** payload 包装', !!pong && !('payload' in pong) && typeof pong.timestamp === 'number', pong)

  // chat.message（requestId 必须在顶层）
  const requestId = `verify_${Date.now()}`
  const m1 = mark()
  ws.send(
    JSON.stringify({
      type: 'chat.message',
      requestId,
      payload: { messageType: 'text', content: '两点叫我睡觉', timestamp: Date.now() }
    })
  )

  await waitFor(() => since(m1).some((f) => f.type === 'chat.reply.stream' && f.payload?.done === true), 12000)

  const echo = since(m1).find((f) => f.type === 'chat.message.echo')
  check('收到 chat.message.echo（多设备回声）', !!echo)
  check('echo 携带顶层 requestId（客户端据此幂等去重，EDGE-L4）', echo?.requestId === requestId, echo?.requestId)
  check('echo payload 含 content / imageCount / timestamp / originDeviceId',
    !!echo && 'content' in echo.payload && 'imageCount' in echo.payload && 'timestamp' in echo.payload && 'originDeviceId' in echo.payload,
    echo?.payload && Object.keys(echo.payload)
  )

  const queued = since(m1).find((f) => f.type === 'chat.queued')
  check('收到 chat.queued 且携带 debounceWindowSec（EDGE-L17 客户端不得硬编码）',
    !!queued && typeof queued.payload?.debounceWindowSec === 'number',
    queued?.payload
  )

  const typingFrames = since(m1).filter((f) => f.type === 'chat.typing')
  check('收到 chat.typing（含 stage）', typingFrames.length > 0, typingFrames.map((f) => f.payload))

  const deltas = since(m1).filter((f) => f.type === 'chat.reply.stream' && f.payload?.done === false)
  check('收到多个非空 delta（FR-CHAT-5 流式）', deltas.length > 1 && deltas.every((f) => f.payload.delta.length > 0), deltas.length)

  const done = since(m1).find((f) => f.type === 'chat.reply.stream' && f.payload?.done === true)
  check('done 帧携带顶层 requestId 与 payload.messageId', done?.requestId === requestId && typeof done.payload.messageId === 'string', { requestId: done?.requestId, messageId: done?.payload?.messageId })
  check('done 帧含 finalContent / timestamp / contentType / modelProvider',
    typeof done.payload.finalContent === 'string' &&
      typeof done.payload.timestamp === 'number' &&
      typeof done.payload.contentType === 'string' &&
      typeof done.payload.modelProvider === 'string',
    Object.keys(done.payload)
  )
  check('FR-CHAT-6：finalContent 含换行（多气泡拆分的数据来源）', done.payload.finalContent.includes('\n'), JSON.stringify(done.payload.finalContent))
  check('FR-CHAT-7：done 帧含 requestIds 数组', Array.isArray(done.payload.requestIds) && done.payload.requestIds.includes(requestId), done.payload.requestIds)
  check('FR-REM-1：done 帧含服务端已解析的 timerInstruction',
    !!done.payload.timerInstruction && /^\d{1,2}:\d{2}$/.test(done.payload.timerInstruction.target),
    done.payload.timerInstruction
  )
  check('FR-CHAT-6：finalContent 内**不含** [[TIMER:...]] 原始标记（服务端已剥离）',
    !done.payload.finalContent.includes('[[TIMER'),
    done.payload.finalContent
  )

  // NFR-12：忽略未知 type
  const mUnknown = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'unknown_frame' })
  }).catch(() => undefined)
  await waitFor(() => since(mUnknown).some((f) => f.type === 'future.feature.event'), 3000)
  check('NFR-12：收到未知 type 的帧后连接仍存活（客户端必须忽略未知字段与未知 type）', ws.readyState === WebSocket.OPEN, ws.readyState)

  /* --------------------------- §7.1a 合并时序 -------------------------- */
  section('§7.1a 「文字 + 送礼」合并时序（EDGE-L22）')

  // 先发文字（进防抖缓冲），立刻点礼物（不带附言）→ 服务端应摘走缓冲并回传 mergedRequestIds
  const chatRequestId = `verify_chat_${Date.now()}`
  ws.send(
    JSON.stringify({
      type: 'chat.message',
      requestId: chatRequestId,
      payload: { messageType: 'text', content: '今天好累', timestamp: Date.now() }
    })
  )
  await sleep(60)

  const giftRequestId = `verify_gift_${Date.now()}`
  const mGift = mark()
  const sendResult = await req('/api/v1/interaction/send', {
    method: 'POST',
    token,
    body: { itemId: 'coffee', requestId: giftRequestId }
  })
  check('互动发送返回 HTTP 200（信封）', sendResult.status === 200 && sendResult.json?.code === 0, sendResult.json?.code)
  const sendData = sendResult.json?.data
  check('互动响应含 success/itemId/requestId/balance/charged/refunded/duplicate',
    typeof sendData?.success === 'boolean' &&
      typeof sendData?.balance === 'number' &&
      typeof sendData?.charged === 'number' &&
      typeof sendData?.refunded === 'boolean' &&
      typeof sendData?.duplicate === 'boolean',
    sendData && Object.keys(sendData)
  )
  check('EDGE-L22：**先发文字再送礼**时返回 mergedRequestIds，客户端须据此把气泡置为已送达',
    Array.isArray(sendData?.mergedRequestIds) && sendData.mergedRequestIds.includes(chatRequestId),
    { mergedRequestIds: sendData?.mergedRequestIds, expected: chatRequestId }
  )
  check('互动响应 mergedCount 与 mergedRequestIds 长度一致',
    sendData?.mergedCount === (sendData?.mergedRequestIds?.length ?? -1),
    { mergedCount: sendData?.mergedCount, len: sendData?.mergedRequestIds?.length }
  )
  check('FR-INT-11：requestId 幂等 —— 重复提交同一 requestId 返回 duplicate=true 且不重复扣分',
    await (async () => {
      const again = await req('/api/v1/interaction/send', {
        method: 'POST',
        token,
        body: { itemId: 'coffee', requestId: giftRequestId }
      })
      return again.json?.data?.duplicate === true && again.json?.data?.balance === sendData?.balance
    })()
  )

  // 互动回复应经既有 chat.reply.stream 通道下发，且带 messageKind=interaction
  // ⚠️ 注意：`POST /interaction/send` 的**响应在服务端推送完流式回复之后**才返回，
  //    因此这些帧在 await 期间就已到达 —— 必须从累积帧里按标记查找，不能先清空。
  await waitFor(() => since(mGift).some((f) => f.type === 'chat.reply.stream' && f.payload?.done === true), 12000)
  const giftDone = since(mGift).find((f) => f.type === 'chat.reply.stream' && f.payload?.done === true)
  check('FR-INT-6：互动回复复用 chat.reply.stream 通道', !!giftDone)
  check('FR-INT-6：互动回复带 messageKind=interaction 与物品图标/名称',
    giftDone?.payload?.messageKind === 'interaction' &&
      !!giftDone?.payload?.interactionItemIcon &&
      !!giftDone?.payload?.interactionItemName,
    { messageKind: giftDone?.payload?.messageKind, icon: giftDone?.payload?.interactionItemIcon }
  )
  check('FR-INT-6：互动回复的 requestIds 含被合并的用户 requestId',
    Array.isArray(giftDone?.payload?.requestIds) && giftDone.payload.requestIds.includes(giftRequestId),
    giftDone?.payload?.requestIds
  )
  const mergeStage = since(mGift).find((f) => f.type === 'chat.typing' && f.payload?.stage === 'interaction_merge')
  const interactionStage = since(mGift).find((f) => f.type === 'chat.typing' && f.payload?.stage === 'interaction')
  check('§7.1a：等待期间先推 stage=interaction_merge，随后 stage=interaction', !!mergeStage && !!interactionStage, {
    mergeStage: !!mergeStage,
    interactionStage: !!interactionStage,
    stages: since(mGift).filter((f) => f.type === 'chat.typing').map((f) => f.payload?.stage)
  })
  check('§7.1a：互动回复流式 delta 也带 messageKind=interaction',
    since(mGift)
      .filter((f) => f.type === 'chat.reply.stream' && f.payload?.done === false)
      .every((f) => f.payload?.messageKind === 'interaction'),
    since(mGift).filter((f) => f.type === 'chat.reply.stream' && f.payload?.done === false).length
  )

  /* ------------------------------ 积分事件 ----------------------------- */
  section('§11.1 WS 推送事件（共 5 个）')

  const mLevel = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'level_upgrade' })
  })
  await waitFor(() => since(mLevel).some((f) => f.type === 'level.changed'), 3000)
  const levelChanged = since(mLevel).find((f) => f.type === 'level.changed')
  check('level.changed 携带 changeType（FR-LV-4/5/5a 三态区分）', levelChanged?.payload?.changeType === 'UPGRADE', levelChanged?.payload?.changeType)
  check('level.changed 携带 continuousDays / gapDays / breakDeadlineDate / daysToNextLevel',
    typeof levelChanged?.payload?.continuousDays === 'number' &&
      typeof levelChanged?.payload?.gapDays === 'number' &&
      'breakDeadlineDate' in (levelChanged?.payload ?? {}) &&
      'daysToNextLevel' in (levelChanged?.payload ?? {}),
    levelChanged?.payload && Object.keys(levelChanged.payload)
  )

  const mStreak = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'streak_warning' })
  })
  await waitFor(() => since(mStreak).some((f) => f.type === 'streak.warning'), 3000)
  const streak = since(mStreak).find((f) => f.type === 'streak.warning')
  check('streak.warning 携带 gapDays / remainingDays / deadlineDate（FR-LV-6）',
    typeof streak?.payload?.gapDays === 'number' &&
      typeof streak?.payload?.remainingDays === 'number' &&
      typeof streak?.payload?.deadlineDate === 'string',
    streak?.payload
  )

  const mMakeup = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'makeup_grant' })
  })
  await waitFor(() => since(mMakeup).some((f) => f.type === 'makeup_card.changed'), 3000)
  const makeupChanged = since(mMakeup).find((f) => f.type === 'makeup_card.changed')
  check('makeup_card.changed 携带 reason/available/maxAvailable（FR-MC-8）',
    typeof makeupChanged?.payload?.reason === 'string' &&
      typeof makeupChanged?.payload?.available === 'number' &&
      typeof makeupChanged?.payload?.maxAvailable === 'number',
    makeupChanged?.payload
  )

  const mGreet = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'greeting' })
  })
  await waitFor(() => since(mGreet).some((f) => f.type === 'chat.reply.stream' && f.payload?.messageKind === 'greeting'), 3000)
  const greeting = since(mGreet).find((f) => f.type === 'chat.reply.stream' && f.payload?.messageKind === 'greeting')
  check('FR-NOTI-1：问候消息带 messageKind=greeting 与 greetingScenario',
    greeting?.payload?.greetingScenario === 'morning' || greeting?.payload?.greetingScenario === 'night',
    greeting?.payload?.greetingScenario
  )

  const mErr = mark()
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'bot_error' })
  })
  await waitFor(() => since(mErr).some((f) => f.type === 'bot.error'), 3000)
  const botError = since(mErr).find((f) => f.type === 'bot.error')
  check('bot.error 携带 errorCode / message / requestIds / timestamp（§5.3.2）',
    typeof botError?.payload?.errorCode === 'string' &&
      typeof botError?.payload?.message === 'string' &&
      Array.isArray(botError?.payload?.requestIds) &&
      typeof botError?.payload?.timestamp === 'number',
    botError?.payload
  )

  /* ------------------------------ 补签卡闭环 --------------------------- */
  section('§7.4 补签卡闭环')
  const cand = candidates.json?.data?.items ?? []
  if (cand.length > 0) {
    const target = cand[0].date
    const useResult = await req('/api/v1/points/makeup-card/use', { method: 'POST', token, body: { targetDate: target } })
    check('补签成功返回 availableCards / targetDate / card / level', useResult.json?.code === 0 && typeof useResult.json?.data?.availableCards === 'number', useResult.json?.data)
    check('FR-MC-5：补签后 level.changeType 为 RESTORE（走克制反馈，非庆祝）',
      useResult.json?.data?.level?.changeType === 'RESTORE',
      useResult.json?.data?.level?.changeType
    )

    const again = await req('/api/v1/points/makeup-card/use', { method: 'POST', token, body: { targetDate: target } })
    check('40206：重复补签同一天返回 code=40206', again.json?.code === 40206, again.json?.code)
  } else {
    check('补签卡闭环（无候选日期，跳过）', true)
  }

  /* --------------------------- 历史 / 记忆同步 ------------------------- */
  section('§6.5 历史与记忆同步')
  await fetch(`${BASE}/__test__/trigger`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify({ kind: 'seed_history', count: 6 })
  })
  const history = await req('/api/v1/chat/history?since=0&limit=300', { token })
  const historyItems = history.json?.data?.items ?? []
  check('GET /chat/history 返回信封 + items[]', history.json?.code === 0 && Array.isArray(historyItems), history.json?.code)
  check('timeline item 字段为 messageId/userId/role/content/timestamp',
    historyItems.every((i) => 'messageId' in i && 'userId' in i && 'role' in i && 'content' in i && 'timestamp' in i),
    historyItems[0] && Object.keys(historyItems[0])
  )
  check('role ∈ {user, bot}', historyItems.every((i) => i.role === 'user' || i.role === 'bot'), [...new Set(historyItems.map((i) => i.role))])

  const facts = await req('/api/v1/memory/facts?since=0&limit=300', { token })
  check('GET /memory/facts 返回信封 + items[]', facts.json?.code === 0 && Array.isArray(facts.json?.data?.items), facts.json?.code)

  /* -------------------------------- 收尾 ------------------------------ */
  ws.close()

  console.log(`\n${'─'.repeat(70)}`)
  if (fail === 0) {
    console.log(`\x1b[32m\x1b[1m全部通过：${pass} 项断言\x1b[0m\n`)
  } else {
    console.log(`\x1b[31m\x1b[1m失败 ${fail} 项 / 共 ${pass + fail} 项\x1b[0m`)
    for (const name of failures) console.log(`  \x1b[31m·\x1b[0m ${name}`)
    console.log()
  }
  process.exit(fail === 0 ? 0 : 1)
}

function waitFor(predicate, timeoutMs = 3000, intervalMs = 25) {
  const start = Date.now()
  return new Promise((resolve) => {
    const tick = () => {
      if (predicate()) return resolve(true)
      if (Date.now() - start > timeoutMs) return resolve(false)
      setTimeout(tick, intervalMs)
    }
    tick()
  })
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms))
}

main().catch((err) => {
  console.error(`\n\x1b[31m自检异常终止：\x1b[0m ${err.message}`)
  console.error(err.stack)
  process.exit(1)
})
