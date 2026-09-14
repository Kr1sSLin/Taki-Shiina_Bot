/**
 * 聊天服务（§6.3 对话核心、§6.4 图片、§6.5 历史同步、§10.2 消息状态机、§10.3 时序）。
 *
 * 承担：
 * - 乐观发送与状态回写（FR-CHAT-3）
 * - 流式渲染占位消息（FR-CHAT-5）与多气泡拆分（FR-CHAT-6）
 * - 防抖合并后的批量送达标记（FR-CHAT-7）与 §7.1a 的 `mergedRequestIds`（EDGE-L22）
 * - 客户端流式超时 150s（FR-CHAT-8）
 * - 断线时把在途请求标记为 `CONNECTION_LOST`（FR-CONN-8 / FR-NET-2）
 * - `bot.error` 落 `bot_notifications` 并弹通知（§5.3.2）
 * - 互动回复三路到达去重（FR-INT-12 / EDGE-L21）
 * - 历史增量/全量同步（FR-SYNC-1..9）
 */

import { randomUUID } from 'node:crypto'
import type {
  ChatMessage,
  ChatReplyStreamPayload,
  TimerInstruction,
  TimelineItem,
  UserFact,
  WsBotErrorFrame,
  WsEchoFrame,
  WsMemoryFactFrame,
  WsQueuedFrame,
  WsReplyStreamFrame,
  WsTypingFrame
} from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import {
  IPC,
  type StreamDeltaEvent,
  type StreamDoneEvent,
  type SyncStatusEvent,
  type TypingEvent
} from '@shared/ipc'
import { bus } from '../../app/bus'
import { createLogger } from '../../app/logger'
import { readFileAsBase64 } from './image-service'
import type { ConnectionManager } from '../network/connection-manager'
import type { RestClient } from '../network/rest-client'
import {
  appendStreamingDelta,
  clearConversationSession,
  countMessages,
  createStreamingPlaceholder,
  deleteMessage as repoDeleteMessage,
  deletePendingMessage,
  getMessage,
  isDelivered,
  listAttachmentsByMessageIds,
  listMessages,
  markDelivered,
  maxTimestamp,
  pendingMessageId,
  rebindAttachments,
  searchMessages,
  setCursor,
  splitBotContent,
  stripHistoryTimestampPrefix,
  updateMessageStatus,
  upsertMessage,
  getCursor
} from '../database/repositories/message-repository'
import { bulkUpsertFacts, maxFactTimestamp, upsertFact } from '../database/repositories/notification-repository'

const log = createLogger('chat')

export interface ChatServiceDeps {
  conn: ConnectionManager
  rest: RestClient
  /** 排程提醒（FR-REM-1）。返回是否为新排程。 */
  scheduleReminder: (input: { reminderId: string; targetTime: string; fireAt: number; text: string }) => boolean
  /** 计算提醒目标时刻（FR-REM-2）。 */
  computeReminderFireAt: (target: string) => number
  /** 落库 Bot 错误通知（§9.3）。 */
  insertBotNotification: (input: { errorCode: string; message: string; timestamp: number }) => void
  /**
   * FR-NOTI-1：Bot 回复到达时的桌面通知（`chat` / `greeting` 两类渠道）。
   * **只在本次是首次落库时调用**（`messages.length > 0`），
   * 这样「WS 推送 / HTTP reply / 历史补拉」三路到达时不会重复弹通知（EDGE-L21）。
   */
  notifyReply: (info: {
    messages: ChatMessage[]
    messageKind: string | null
    greetingScenario: string | null
    interactionItemName: string | null
    interactionItemIcon: string | null
  }) => void
  /** FR-NOTI-1：Bot 错误到达时的桌面通知（`error` 渠道）。 */
  notifyError: (info: { errorCode: string; message: string }) => void
  /** 回复完成后触发积分/等级刷新。 */
  onAfterReply: () => void
  /** 同步完成后触发积分/等级刷新。 */
  onAfterSync: () => void
}

interface StreamingEntry {
  requestId: string
  content: string
  contentType: 'text' | 'image' | 'mixed'
  modelProvider: string
  startedAt: number
}

export class ChatService {
  private deps: ChatServiceDeps
  private streaming = new Map<string, StreamingEntry>()
  private timers = new Map<string, NodeJS.Timeout>()
  /** requestId → 防抖窗口秒数（EDGE-L17：一律使用服务端下发值，不硬编码 8/20）。 */
  private queued = new Map<string, number>()
  private typing: TypingEvent = { typing: false }
  private syncInFlight: Promise<SyncStatusEvent> | null = null

  constructor(deps: ChatServiceDeps) {
    this.deps = deps
  }

  /* ------------------------------------------------------------------ 发送 */

  /**
   * 发送一条用户消息（FR-CHAT-2/3/4）。
   *
   * @param input.requestId 渲染进程生成的 `uuidv4`，作为本地主键 + WS 请求 ID + 幂等键
   * @param input.attachmentIds 草稿附件 ID（已复制到应用私有目录，FR-IMG-5）
   */
  send(input: { requestId: string; content: string; attachmentIds: string[] }): void {
    const content = (input.content ?? '').trim()
    const attachmentIds = input.attachmentIds ?? []

    // FR-CHAT-4：未连接时禁止发送，不做离线队列（理由见 EDGE-L6 / 服务端防抖基于服务端时间）
    if (!this.deps.conn.isConnected()) throw new Error('NOT_CONNECTED')
    if (!content && attachmentIds.length === 0) throw new Error('EMPTY_MESSAGE')

    const attachments = listAttachmentsByMessageIds(attachmentIds)
    const hasImages = attachments.length > 0
    const now = Date.now()

    // FR-CHAT-3：乐观入库（status=sending）
    upsertMessage({
      messageId: input.requestId,
      sessionId: PROTOCOL.DEFAULT_SESSION_ID,
      role: 'user',
      messageType: hasImages ? 'image' : 'text',
      contentType: hasImages ? (content ? 'mixed' : 'image') : 'text',
      content,
      status: 'sending',
      timestamp: now
    })

    // FR-IMG-6：把草稿附件挂到真实消息上（此前 message_id 指向草稿占位）
    if (attachmentIds.length > 0) rebindAttachments(attachmentIds, input.requestId)

    const ok = this.deps.conn.send({
      type: 'chat.message',
      // ⚠️ requestId 必须在**顶层**：后端 `message.get("requestId")` 读的是顶层字段，
      //    放在 payload 里会导致回声与幂等去重全部失效。
      requestId: input.requestId,
      payload: {
        messageType: hasImages ? 'image' : 'text',
        content: content || undefined,
        images: hasImages
          ? attachments.map((a) => ({
              mimeType: a.mimeType as 'image/jpeg' | 'image/png',
              dataBase64: readFileAsBase64(a.localPath),
              localUri: a.localPath
            }))
          : undefined,
        timestamp: now
      }
    })

    updateMessageStatus(input.requestId, ok ? 'sent' : 'error', ok ? null : 'SEND_FAILED')
    this.emitMessages([input.requestId], [])

    if (ok) {
      log.info('消息已发送', { requestId: input.requestId, images: attachments.length, chars: content.length })
    } else {
      log.warn('消息发送失败（SEND_FAILED）', { requestId: input.requestId })
    }
  }

  /**
   * FR-CHAT-9：REST 降级通道（`POST /api/v1/chat`，非流式）。
   * 仅在 WS 连续重连失败达上限（`Degraded`）后由 UI 显式使用；
   * 注意该接口**不参与积分结算**，由 UI 明示。
   */
  async sendViaRest(input: { requestId: string; content: string }): Promise<{ reply: string; messages: ChatMessage[] }> {
    const content = (input.content ?? '').trim()
    if (!content) throw new Error('EMPTY_MESSAGE')

    upsertMessage({
      messageId: input.requestId,
      sessionId: PROTOCOL.DEFAULT_SESSION_ID,
      role: 'user',
      messageType: 'text',
      contentType: 'text',
      content,
      status: 'sending',
      timestamp: Date.now()
    })
    this.emitMessages([input.requestId], [])

    try {
      const data = await this.deps.rest.request<{ reply: string; conversationId?: string; messageId?: string }>(
        'chat',
        {
          method: 'POST',
          body: { requestId: input.requestId, message: content, stream: false }
        }
      )
      updateMessageStatus(input.requestId, 'sent')
      /*
       * ⚠️ 必须用服务端返回的 `messageId` 作为 Bot 消息主键。
       *
       * 服务端已把这条回复写进时间线（客户端 `GET /chat/history` 读的就是它）。
       * 若这里自己编一个 `rest_{uuid}`，本地 `delivered_bot_messages` 里记的就是
       * 那个自编 id，而时间线里的 id 是服务端的 uuid —— 下一次同步会认为
       * 「这条回复还没投递过」而**再插一条同样的气泡**，界面永久重复。
       * 只有在服务端没回 messageId（旧版本服务端）时才回退到本地生成。
       */
      const messages = this.persistBotReply({
        finalMessageId: data?.messageId ?? `rest_${randomUUID()}`,
        content: data?.reply ?? '',
        timestamp: Date.now(),
        contentType: 'text',
        modelProvider: 'deepseek',
        messageKind: null
      })
      this.emitMessages([input.requestId, ...messages.map((m) => m.messageId)], [])
      this.deps.onAfterReply()
      return { reply: data?.reply ?? '', messages }
    } catch (err) {
      updateMessageStatus(input.requestId, 'error', 'SEND_FAILED')
      this.emitMessages([input.requestId], [])
      throw err
    }
  }

  /* -------------------------------------------------------- 服务端帧处理 */

  /** `chat.message.echo`：多设备回声，按 `requestId` 幂等（EDGE-L4）。 */
  handleEcho(frame: WsEchoFrame): void {
    const requestId = frame.requestId
    if (!requestId) return

    const existing = getMessage(requestId)
    if (existing) {
      // ⚠️ 自己发的消息也会收到自己的回声 → 绝不能重复上屏
      if (existing.status === 'sending') {
        updateMessageStatus(requestId, 'sent')
        this.emitMessages([requestId], [])
      }
      return
    }

    const payload = frame.payload
    const imageCount = payload?.imageCount ?? 0
    upsertMessage({
      messageId: requestId,
      sessionId: PROTOCOL.DEFAULT_SESSION_ID,
      role: 'user',
      messageType: imageCount > 0 ? 'image' : 'text',
      contentType: imageCount > 0 ? 'image' : 'text',
      content: payload?.content ?? '',
      status: 'sent',
      timestamp: payload?.timestamp ?? Date.now()
    })
    log.info('收到其他设备回声', { requestId, originDeviceId: payload?.originDeviceId, imageCount })
    this.emitMessages([requestId], [])
  }

  /** `chat.queued`：展示「已排队（{debounceWindowSec}s 内合并）」（FR-CHAT-15 / EDGE-L17）。 */
  handleQueued(frame: WsQueuedFrame): void {
    const sec = frame.payload?.debounceWindowSec
    if (typeof sec !== 'number') return
    this.queued.set(frame.requestId, sec)
    bus.send(IPC.evtQueued, { requestId: frame.requestId, debounceWindowSec: sec })
  }

  /** `chat.typing`：按 `stage` 区分文案（FR-CHAT-14 / FR-IMG-10）。 */
  handleTyping(frame: WsTypingFrame): void {
    const payload = frame.payload ?? { typing: false }
    this.typing = { typing: !!payload.typing, stage: payload.stage }
    if (!payload.typing) this.queued.clear()
    bus.send(IPC.evtTyping, this.typing)
  }

  /** `chat.reply.stream`：流式主通道（§10.3）。 */
  handleReplyStream(frame: WsReplyStreamFrame): void {
    const requestId = frame.requestId
    const payload = frame.payload
    if (!requestId || !payload) return

    if (!payload.done) {
      const delta = payload.delta ?? ''
      if (!delta) return

      // FR-CHAT-5：首个非空 delta 时创建占位消息 `pending_{requestId}`（status=streaming）
      if (!this.streaming.has(requestId)) {
        const contentType = (payload.contentType ?? 'text') as 'text' | 'image' | 'mixed'
        const modelProvider = payload.modelProvider ?? 'deepseek'
        createStreamingPlaceholder({
          requestId,
          sessionId: PROTOCOL.DEFAULT_SESSION_ID,
          contentType,
          modelProvider,
          timestamp: Date.now()
        })
        this.streaming.set(requestId, { requestId, content: '', contentType, modelProvider, startedAt: Date.now() })
        this.emitMessages([pendingMessageId(requestId)], [])
      }

      const entry = this.streaming.get(requestId)
      if (entry) entry.content += delta
      appendStreamingDelta(requestId, delta)
      // FR-CHAT-8：每收到一个 delta 重置 150s 计时
      this.resetStreamingTimer(requestId)

      bus.send(IPC.evtStreamDelta, {
        requestId,
        delta,
        contentType: payload.contentType ?? 'text',
        modelProvider: payload.modelProvider ?? 'deepseek'
      } satisfies StreamDeltaEvent)
      this.emitMessages([pendingMessageId(requestId)], [])
      return
    }

    this.finishStream(requestId, payload)
  }

  /** `bot.error`：标记错误态 + 落 `bot_notifications` + 弹通知（§5.3.2）。 */
  handleBotError(frame: WsBotErrorFrame): void {
    const payload = frame.payload
    const code = payload?.errorCode ?? 'INTERNAL_ERROR'
    const message = payload?.message ?? ''
    const timestamp = payload?.timestamp ?? Date.now()
    const requestIds = payload?.requestIds?.length ? payload.requestIds : frame.requestId ? [frame.requestId] : []

    log.warn('服务端错误', { code, requestIds })

    const removed: string[] = []
    for (const requestId of requestIds) {
      updateMessageStatus(requestId, 'error', code)
      deletePendingMessage(requestId)
      removed.push(pendingMessageId(requestId))
      this.clearStreaming(requestId)
    }
    if (requestIds.length > 0) this.emitMessages(requestIds, removed)

    this.deps.insertBotNotification({ errorCode: code, message, timestamp })
    bus.send(IPC.evtNotificationCreated, { errorCode: code, message, timestamp })
    // FR-NOTI-1：错误类走独立通知渠道（Android 端复用聊天渠道，本端按 PRD 单列）
    this.deps.notifyError({ errorCode: code, message })
  }

  /** `memory.fact.created`：实时写入 `user_facts` 并推进游标（FR-SYNC-7）。 */
  handleMemoryFact(frame: WsMemoryFactFrame): void {
    const fact: UserFact | undefined = frame.payload
    if (!fact?.factId) return
    upsertFact(fact)
    setCursor('memory_facts', fact.timestamp)
    bus.send(IPC.evtFactsUpdated, { facts: [fact] })
  }

  /* ------------------------------------------------------------- 流式收尾 */

  private resetStreamingTimer(requestId: string): void {
    const existing = this.timers.get(requestId)
    if (existing) clearTimeout(existing)
    const timer = setTimeout(() => {
      // FR-CHAT-8：150s 无新 delta → TIMEOUT，提示「AI 响应超时，请重试」
      log.warn('流式超时（150s 无新 delta）', { requestId })
      const entry = this.streaming.get(requestId)
      if (!entry) return
      // ⚠️ `deletePendingMessage` 内部会自行拼 `pending_` 前缀，这里必须传**原始 requestId**；
      //    传 `pendingMessageId(requestId)` 会去删 `pending_pending_xxx`，占位行永远删不掉。
      const pendingId = pendingMessageId(requestId)
      this.clearStreaming(requestId)
      deletePendingMessage(requestId)
      updateMessageStatus(requestId, 'error', 'TIMEOUT')
      this.emitMessages([requestId], [pendingId])
      bus.send(IPC.evtStreamDone, {
        requestId,
        message: null,
        allMessages: [],
        requestIds: [requestId],
        timerInstruction: null,
        messageKind: null,
        interactionItemIcon: null,
        interactionItemName: null,
        interactionFailed: false,
        errorCode: 'TIMEOUT'
      })
    }, PROTOCOL.STREAMING_TIMEOUT_MS)
    timer.unref?.()
    this.timers.set(requestId, timer)
  }

  private clearStreaming(requestId: string): void {
    const timer = this.timers.get(requestId)
    if (timer) clearTimeout(timer)
    this.timers.delete(requestId)
    this.streaming.delete(requestId)
  }

  /**
   * `done=true` 收尾（§10.3）：取消超时 → 按 `requestIds` 批量标记已送达 →
   * 删占位 → 拆分落库 → 排程提醒 → 触发记忆补拉。
   */
  private finishStream(requestId: string, payload: ChatReplyStreamPayload): void {
    const entry = this.streaming.get(requestId)
    this.clearStreaming(requestId)

    this.typing = { typing: false }
    bus.send(IPC.evtTyping, this.typing)

    const finalMessageId = payload.messageId ?? `bot_${randomUUID()}`
    // 优先用 `finalContent`（服务端已清洗）；缺失时回退到累积的 delta
    const finalContent = (payload.finalContent ?? entry?.content ?? '').trim()
    const timestamp = payload.timestamp ?? Date.now()
    const pendingId = pendingMessageId(requestId)

    // ⚠️ 必须传原始 `requestId`：`deletePendingMessage` 内部会拼 `pending_` 前缀。
    //    这里曾经误传 `pendingId`，导致每次正常收尾都去删 `pending_pending_xxx`，
    //    占位消息永久残留为 `streaming`（已由集成自检捕获）。
    deletePendingMessage(requestId)

    // FR-CHAT-7：一次回复可能对应多条被防抖合并的用户消息
    const requestIds = payload.requestIds?.length ? payload.requestIds : [requestId]
    const touchedUsers: string[] = []
    for (const id of requestIds) {
      const msg = getMessage(id)
      if (msg && msg.status !== 'sent' && msg.role === 'user') {
        updateMessageStatus(id, 'sent')
        touchedUsers.push(id)
      }
    }

    // FR-CHAT-10：空内容不得落库（服务端已实现重采样与 `……` 占位）
    let messages: ChatMessage[] = []
    if (finalContent) {
      messages = this.persistBotReply({
        finalMessageId,
        content: finalContent,
        timestamp,
        contentType: (payload.contentType ?? 'text') as 'text' | 'image' | 'mixed',
        modelProvider: payload.modelProvider ?? 'deepseek',
        messageKind: payload.messageKind ?? null,
        interactionItemIcon: payload.interactionItemIcon ?? null,
        interactionItemName: payload.interactionItemName ?? null
      })
    } else {
      log.warn('收到空 Bot 回复，已丢弃（FR-CHAT-10）', { requestId })
    }

    // FR-REM-1：只消费服务端已解析的 `timerInstruction`，客户端不写 `[[TIMER:...]]` 正则
    if (payload.timerInstruction?.target) {
      this.scheduleFromInstruction(payload.timerInstruction, requestIds)
    }

    this.emitMessages([...touchedUsers, ...messages.map((m) => m.messageId)], [pendingId])
    bus.send(IPC.evtStreamDone, {
      requestId,
      message: messages[0] ?? null,
      allMessages: messages,
      requestIds,
      timerInstruction: payload.timerInstruction ?? null,
      messageKind: payload.messageKind ?? null,
      interactionItemIcon: payload.interactionItemIcon ?? null,
      interactionItemName: payload.interactionItemName ?? null,
      interactionFailed: !!payload.interactionFailed
    } satisfies StreamDoneEvent)

    // FR-NOTI-1 / FR-NOTI-2：回复到达时弹桌面通知。
    // 仅在本次**首次**落库时通知（`messages` 为空说明该回复已由其它路径投递过），
    // 否则「WS 推送 / HTTP reply / 历史补拉」三路到达会重复弹三条（EDGE-L21）。
    // 窗口前台聚焦时的抑制由 NotificationService 内部负责（FR-NOTI-2）。
    if (messages.length > 0) {
      this.deps.notifyReply({
        messages,
        messageKind: payload.messageKind ?? null,
        greetingScenario: payload.greetingScenario ?? null,
        interactionItemName: payload.interactionItemName ?? null,
        interactionItemIcon: payload.interactionItemIcon ?? null
      })
    }

    // FR-SYNC-7：每次回复完成后触发一次记忆增量补拉
    void this.pullFacts().catch(() => undefined)
    this.deps.onAfterReply()
  }

  /**
   * 落库 Bot 回复（多气泡拆分，FR-CHAT-6 / FR-SYNC-5）。
   * 通过 `delivered_bot_messages` 去重：同一条回复可能经
   * 「WS 推送」「HTTP 响应 reply」「历史补拉」三路到达（FR-INT-12 / EDGE-L21）。
   */
  private persistBotReply(input: {
    finalMessageId: string
    content: string
    timestamp: number
    contentType: 'text' | 'image' | 'mixed'
    modelProvider: string
    messageKind: string | null
    interactionItemIcon?: string | null
    interactionItemName?: string | null
  }): ChatMessage[] {
    const parts = splitBotContent(input.finalMessageId, input.content, input.timestamp)
    if (parts.length === 0) return []

    const out: ChatMessage[] = []
    for (const part of parts) {
      if (isDelivered(part.messageId)) continue
      upsertMessage({
        messageId: part.messageId,
        sessionId: PROTOCOL.DEFAULT_SESSION_ID,
        role: 'bot',
        messageType: 'text',
        contentType: input.contentType,
        modelProvider: input.modelProvider,
        content: part.content,
        status: 'received',
        timestamp: part.timestamp
      })
      markDelivered([part.messageId])
      out.push({
        messageId: part.messageId,
        sessionId: PROTOCOL.DEFAULT_SESSION_ID,
        role: 'bot',
        messageType: 'text',
        contentType: input.contentType,
        modelProvider: input.modelProvider,
        content: part.content,
        status: 'received',
        timestamp: part.timestamp,
        errorCode: null,
        messageKind: (input.messageKind as ChatMessage['messageKind']) ?? null,
        interactionItemIcon: input.interactionItemIcon ?? null,
        interactionItemName: input.interactionItemName ?? null
      })
    }
    return out
  }

  private scheduleFromInstruction(instruction: TimerInstruction, requestIds: string[]): void {
    const fireAt = this.deps.computeReminderFireAt(instruction.target)
    // FR-REM-3：`reminder_{requestIds.join("_")}_{target}_{hash(text)}`
    // （PRD 有意用 requestIds 拼接以兼容防抖合并，优于 Android 的单 requestId 实现）
    const reminderId = `reminder_${requestIds.join('_')}_${instruction.target}_${simpleHash(instruction.text)}`
    const created = this.deps.scheduleReminder({
      reminderId,
      targetTime: instruction.target,
      fireAt,
      text: instruction.text
    })
    log.info(created ? '已排程提醒' : '提醒已存在，保留原计划（KEEP 语义）', {
      reminderId,
      target: instruction.target,
      fireAt: new Date(fireAt).toISOString()
    })
  }

  /* ------------------------------------------------- 互动发送结果处理 */

  /**
   * §7.1a / EDGE-L22：`POST /interaction/send` 返回的 `mergedRequestIds`
   * 是那些「先发文字、防抖未到就点礼物」而被服务端**摘走**并合进礼物回复的消息。
   * 必须批量置为已送达，否则它们会永远停在「发送中」。
   */
  applyMergedRequestIds(mergedRequestIds: string[]): void {
    if (!mergedRequestIds?.length) return
    const touched: string[] = []
    for (const id of mergedRequestIds) {
      const msg = getMessage(id)
      if (msg && msg.status !== 'sent') {
        updateMessageStatus(id, 'sent')
        touched.push(id)
      }
    }
    log.info('批量标记被合并的用户消息为已送达', { total: mergedRequestIds.length, touched: touched.length })
    if (touched.length) this.emitMessages(touched, [])
  }

  /* ------------------------------------------------- 连接生命周期回调 */

  /**
   * FR-CONN-8：连接断开时立即清理所有在途请求状态（防止 UI 永久停在「打字中」），
   * 并把它们标记为 `CONNECTION_LOST`。
   *
   * FR-NET-2：服务端**不会**取消防抖 worker，会继续跑完生成并写入服务端时间线。
   * 因此这里**丢弃**流式占位消息（而不是把它提升为 `received`）：
   *   - 占位消息的 ID 是 `pending_{requestId}`，与服务端正式的 `messageId` 不同；
   *     若保留，重连后的全量同步会把**同一条回复的正式消息**再拉回来，
   *     用户就会看到「半截回复 + 完整回复」两份内容。
   *   - 服务端保证回复最终会写入时间线，所以正确的做法是丢弃占位 + 重连后全量同步拉回。
   *
   * 与之相对，EDGE-L7 的**启动期**规整遵循 PRD 原文（有内容转 received、无内容删除），
   * 因为那时没有「重连后全量同步」这一后续动作可以依赖。
   */
  handleConnectionLost(): void {
    const activeRequestIds = [...this.streaming.keys()]
    const removedPending: string[] = []
    const touched: string[] = []

    for (const requestId of activeRequestIds) {
      const pendingId = pendingMessageId(requestId)
      const placeholder = getMessage(pendingId)
      const hadContent = !!placeholder && placeholder.content.trim().length > 0

      this.clearStreaming(requestId)

      // 丢弃占位（无论是否有半截内容），避免与全量同步拉回的正式消息重复
      if (placeholder) {
        deletePendingMessage(requestId)
        removedPending.push(pendingId)
      }

      // 对应的用户消息标记为 CONNECTION_LOST
      const userMsg = getMessage(requestId)
      if (userMsg && userMsg.status !== 'error') {
        updateMessageStatus(requestId, 'error', 'CONNECTION_LOST')
        touched.push(requestId)
      }

      if (hadContent) {
        log.info('断线丢弃半截流式占位，等待重连后全量同步拉回正式回复', { requestId })
      }
    }

    // 兜底：仍处于 sending 的用户消息也标记为连接丢失
    const stillSending = listMessages({ limit: 200 }).filter((m) => m.role === 'user' && m.status === 'sending')
    for (const msg of stillSending) {
      updateMessageStatus(msg.messageId, 'error', 'CONNECTION_LOST')
      touched.push(msg.messageId)
    }

    this.typing = { typing: false }
    this.queued.clear()
    bus.send(IPC.evtTyping, this.typing)
    if (touched.length || removedPending.length) this.emitMessages(touched, removedPending)

    if (activeRequestIds.length || stillSending.length) {
      log.warn('连接断开，已标记在途请求为 CONNECTION_LOST', {
        streaming: activeRequestIds.length,
        pendingUser: stillSending.length,
        droppedPlaceholders: removedPending.length
      })
    }
  }

  /* ------------------------------------------------------------- 读取接口 */

  async list(opts: { limit: number; beforeTimestamp?: number }): Promise<ChatMessage[]> {
    return listMessages({ limit: opts.limit, beforeTimestamp: opts.beforeTimestamp })
  }

  deleteMessage(messageId: string): void {
    repoDeleteMessage(messageId)
    this.emitMessages([], [messageId])
  }

  search(keyword: string, limit = 100): ChatMessage[] {
    return searchMessages(keyword, limit)
  }

  pendingState(): { typing: boolean; stage?: string; queuedRequestIds: string[] } {
    return {
      typing: this.typing.typing,
      stage: this.typing.stage,
      queuedRequestIds: [...this.queued.keys()]
    }
  }

  /* --------------------------------------------------------------- 同步 */

  /**
   * 历史同步（FR-SYNC-1..6、FR-SYNC-8/9）。
   *
   * - 增量：`since = 本地最大 timestamp`，`limit=300`（FR-SYNC-3）
   * - 全量：`since = 0`（手动重连 / 首次登录 / 上次全量同步失败，FR-SYNC-4）
   * - 落库幂等：按 `messageId` 判重；Bot 消息同样按 `\n` 拆分（FR-SYNC-5）
   * - 失败必须写日志且 UI 可见（FR-SYNC-6）
   */
  async syncHistory(full: boolean): Promise<SyncStatusEvent> {
    if (this.syncInFlight) {
      log.debug('同步已在途，复用')
      return this.syncInFlight
    }
    this.syncInFlight = this.doSync(full).finally(() => {
      this.syncInFlight = null
    })
    return this.syncInFlight
  }

  private async doSync(full: boolean): Promise<SyncStatusEvent> {
    const mode: 'incremental' | 'full' = full ? 'full' : 'incremental'
    const cursorBefore = getCursor('chat_history')
    const localMax = maxTimestamp()
    /*
     * FR-SYNC-3 说「以本地最大 timestamp 作为 since」，
     * FR-CHAT-12 又要求「清空会话后把游标推到 now，防止历史被立刻补拉回灌」——
     * 只取本地最大时间戳会让后者失效：清空后本地最大值为 0，于是 since=0，
     * 下一次增量同步就把全部历史又拉回来（Android 端正是踩了这个坑）。
     *
     * 因此取**两者的较大值**：既保证不漏（游标可能落后于实际已落库的消息），
     * 又保证清空后不会回灌（游标已被推进到 now）。
     */
    const since = full ? 0 : Math.max(localMax, cursorBefore)
    log.info('开始历史同步', { mode, since, cursorBefore, localMax, localCount: countMessages() })

    try {
      const data = await this.deps.rest.request<{ items: TimelineItem[] }>('chat/history', {
        query: { since, limit: PROTOCOL.SYNC_LIMIT },
        // 幂等 GET，连接层抖动时退避重试（问题 1 的直接缓解）
        retries: 2
      })
      const items = data?.items ?? []
      const { inserted, updated } = this.ingestTimeline(items)

      const maxTs = items.reduce((acc, it) => Math.max(acc, Number(it.timestamp) || 0), cursorBefore)
      if (maxTs > 0) setCursor('chat_history', maxTs)

      log.info('历史同步完成', { mode, fetched: items.length, inserted, updated })
      const result: SyncStatusEvent = { ok: true, mode, inserted, updated, errorI18nKey: null }
      bus.send(IPC.evtSyncStatus, result)
      /*
       * 问题 3：不能只在 inserted > 0 时刷新。
       * 把一条 `error` 消息修复成 `sent` 走的是 UPDATE 分支（inserted 为 0），
       * 若不刷新，库里已经修好、界面仍显示「发送失败」直到重启。
       */
      if (inserted > 0 || updated > 0) this.emitMessages([], [])
      this.deps.onAfterSync()
      return result
    } catch (err) {
      // FR-SYNC-6：同步失败必须写入日志且在 UI 可见（Android 早期版本静默吞掉导致排查困难）
      log.error('历史同步失败', { mode, error: err instanceof Error ? err.message : String(err) })
      const result: SyncStatusEvent = { ok: false, mode, inserted: 0, updated: 0, errorI18nKey: 'sync.failed' }
      bus.send(IPC.evtSyncStatus, result)
      return result
    }
  }

  /**
   * 把服务端 timeline 落库。
   *
   * FR-SYNC-5：Bot 消息同样按 `\n` 拆分并使用 `{messageId}_{index}` 作为主键，
   * 与实时链路共用 `splitBotContent`，避免同一条消息以两种形态重复入库。
   */
  private ingestTimeline(items: TimelineItem[]): { inserted: number; updated: number } {
    let inserted = 0
    let updated = 0
    for (const item of items) {
      if (!item?.messageId) continue
      // FR-SYNC-9：剥离 `【MM-DD HH:MM】` 前缀
      const content = stripHistoryTimestampPrefix(item.content ?? '')
      const ts = Number(item.timestamp) || Date.now()

      if (item.role === 'bot') {
        const parts = splitBotContent(item.messageId, content, ts)
        for (const part of parts) {
          if (isDelivered(part.messageId)) continue
          const created = upsertMessage({
            messageId: part.messageId,
            sessionId: PROTOCOL.DEFAULT_SESSION_ID,
            role: 'bot',
            messageType: 'text',
            contentType: 'text',
            modelProvider: null,
            content: part.content,
            status: 'received',
            timestamp: part.timestamp
          })
          markDelivered([part.messageId])
          if (created === 'inserted') inserted += 1
          else if (created === 'updated') updated += 1
        }
      } else {
        const created = upsertMessage({
          messageId: item.messageId,
          sessionId: PROTOCOL.DEFAULT_SESSION_ID,
          role: item.role === 'system' ? 'system' : 'user',
          messageType: 'text',
          contentType: 'text',
          modelProvider: null,
          content,
          status: 'sent',
          timestamp: ts
        })
        if (created === 'inserted') inserted += 1
        else if (created === 'updated') updated += 1
      }
    }
    return { inserted, updated }
  }

  /** FR-SYNC-7：用户事实增量补拉（独立游标）。 */
  async pullFacts(): Promise<number> {
    const since = maxFactTimestamp()
    try {
      const data = await this.deps.rest.request<{ items: UserFact[] }>('memory/facts', {
        query: { since, limit: PROTOCOL.SYNC_LIMIT }
      })
      const items = data?.items ?? []
      if (items.length === 0) return 0
      const inserted = bulkUpsertFacts(items)
      const maxTs = items.reduce((acc, it) => Math.max(acc, Number(it.timestamp) || 0), since)
      setCursor('memory_facts', maxTs)
      if (inserted > 0) {
        bus.send(IPC.evtFactsUpdated, { facts: items })
        log.info('记忆增量同步完成', { fetched: items.length, inserted })
      }
      return inserted
    } catch (err) {
      log.warn('记忆补拉失败', { error: String(err) })
      return 0
    }
  }

  /**
   * FR-CHAT-12：清空会话，并把同步游标推到 `now`，防止立刻被历史补拉回灌。
   * ⚠️ 聊天页「清空会话」与设置页「清空本地会话」**共用本方法**。
   */
  clearConversation(): { deleted: number; cursor: number } {
    const result = clearConversationSession({ advanceCursor: true })
    for (const id of [...this.timers.keys()]) this.clearStreaming(id)
    this.typing = { typing: false }
    this.queued.clear()
    bus.send(IPC.evtTyping, this.typing)
    this.emitMessages([], [])
    log.info('已清空本地会话并推进游标', result)
    return result
  }

  private emitMessages(updatedIds: string[], removedIds: string[]): void {
    const messages: ChatMessage[] = []
    for (const id of updatedIds) {
      if (!id) continue
      const msg = getMessage(id)
      if (msg) messages.push(msg)
    }
    bus.send(IPC.evtMessagesUpdated, { messages, removedIds: removedIds.filter(Boolean) })
  }
}

/* -------------------------------------------------------------------------- */

/** FR-REM-3 用的稳定短哈希（跨端一致的 `text.hashCode()` 等价物）。 */
function simpleHash(text: string): string {
  let hash = 0
  for (let i = 0; i < text.length; i += 1) {
    hash = (hash << 5) - hash + text.charCodeAt(i)
    hash |= 0
  }
  return Math.abs(hash).toString(36)
}
