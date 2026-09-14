#!/usr/bin/env node
/**
 * 探针：验证 Mock 后端在「防抖合并」场景下写出的时间线形状。
 *
 * 目的（本次故障复盘）：确认被合并的**每一条**用户消息在 /chat/history 里
 * 都有自己的一行，且 messageId == 客户端 requestId。这是客户端能否把本地
 * `error` 气泡和解回 `sent` 的唯一依据。
 *
 * 用法：node scripts/probe-merge-history.mjs
 */

import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { resolve } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'
import { WebSocket } from 'ws'

const ROOT = resolve(import.meta.dirname, '..')
const PORT = Number(process.env.PROBE_PORT ?? 8799)
const BASE = `http://127.0.0.1:${PORT}`
const USER = 'kris'
const PASS = 'taki'

const mock = spawn('node', ['mock-server/server.mjs'], {
  cwd: ROOT,
  env: { ...process.env, PORT: String(PORT), DEBOUNCE_MS: '600', MERGE_WAIT_MS: '250' },
  stdio: ['ignore', 'pipe', 'pipe']
})
mock.stderr.setEncoding('utf8')
mock.stderr.on('data', (d) => process.stderr.write(`[mock] ${d}`))

async function waitUp() {
  for (let i = 0; i < 50; i += 1) {
    try {
      const res = await fetch(`${BASE}/healthz`)
      if (res.ok) return
    } catch {
      /* retry */
    }
    await sleep(100)
  }
  throw new Error('mock 未启动')
}

async function main() {
  await waitUp()

  const login = await fetch(`${BASE}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ username: USER, password: PASS, deviceId: 'probe-device' })
  })
  const loginBody = await login.json()
  const token = loginBody.accessToken ?? loginBody.data?.accessToken
  if (!token) throw new Error(`登录失败: ${JSON.stringify(loginBody)}`)

  const reqA = randomUUID()
  const reqB = randomUUID()

  const ws = new WebSocket(`ws://127.0.0.1:${PORT}/ws/chat`, { headers: { Authorization: `Bearer ${token}` } })
  const frames = []
  ws.on('message', (raw) => {
    try {
      frames.push(JSON.parse(raw.toString()))
    } catch {
      /* ignore */
    }
  })
  await new Promise((res, rej) => {
    ws.on('open', res)
    ws.on('error', rej)
  })

  ws.send(JSON.stringify({ type: 'chat.message', requestId: reqA, payload: { content: '合并测试第一条', timestamp: Date.now() } }))
  await sleep(80)
  ws.send(JSON.stringify({ type: 'chat.message', requestId: reqB, payload: { content: '合并测试第二条', timestamp: Date.now() } }))

  await sleep(2500)

  const histRes = await fetch(`${BASE}/api/v1/chat/history?since=0&limit=200`, {
    headers: { Authorization: `Bearer ${token}` }
  })
  const hist = await histRes.json()
  const items = hist.data?.items ?? []

  const done = frames.filter((f) => f.type === 'chat.reply.stream' && f.payload?.done)
  const requestIdsInDone = done.flatMap((f) => f.payload?.requestIds ?? [])

  const report = {
    sentRequestIds: [reqA, reqB],
    doneFrameRequestIds: requestIdsInDone,
    userRowsInTimeline: items
      .filter((x) => x.role === 'user')
      .map((x) => ({ messageId: x.messageId, content: x.content, timestamp: x.timestamp })),
    timelineRowCount: items.length,
    bothMergedIdsHaveRows: [reqA, reqB].every((id) => items.some((x) => x.messageId === id)),
    messageIdEqualsRequestId: [reqA, reqB].every((id) => items.some((x) => x.messageId === id))
  }
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`)

  ws.close()
  mock.kill('SIGTERM')
  await sleep(100)
  process.exit(report.bothMergedIdsHaveRows ? 0 : 1)
}

main().catch((err) => {
  process.stderr.write(`探针失败: ${err.message}\n`)
  mock.kill('SIGTERM')
  process.exit(2)
})
