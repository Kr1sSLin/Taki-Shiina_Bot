/**
 * TKS Desktop —— 协议类型定义（主进程 / 渲染进程共用）
 *
 * 来源：PRD《TKS_Linux桌面客户端_PRD_v1.md》v1.2 §5（REST/WS 契约）、§7.0（积分体系接口与字段名契约）。
 * 后端权威契约：`Taki_Shiina_Bot/docs/互动积分等级体系_接口契约.md`。
 *
 * ⚠️ FR-PROTO-1：本文件所有 DTO **逐字段手写**，禁止使用「自动驼峰转换」的通用反序列化。
 *   服务端字段名写错不会抛错，只会静默变成 undefined/默认值（Android 端曾因此闪退）。
 *
 * 字段名风格契约（§7.0）：
 *   - `/level/config` 的 `levels[]`              → snake_case
 *   - `/points/history` 的 `items[]`             → snake_case
 *   - `/points/makeup-card*` 的卡记录             → snake_case
 *   - 其余所有字段                                 → camelCase
 */

/* -------------------------------------------------------------------------- */
/* 通用响应信封                                                                  */
/* -------------------------------------------------------------------------- */

/** 统一响应包络（除 `/auth/*` 外）。业务失败为 HTTP 200 + `code != 0`（§5.2、§7.0）。 */
export interface ApiEnvelope<T> {
  code: number
  message: string
  data: T
  traceId?: string
}

/**
 * `http_api` 的鉴权失败形状陷阱（§5.2）：FastAPI 会把 `HTTPException(detail=...)` 再包一层，
 * 客户端实际收到 `{"detail": {"code": 40101, ...}}`，`code` **不在顶层**。
 * 解析错误码时必须同时兼容「顶层 code」（积分路由）与「detail.code」（http_api）。
 */
export interface ApiErrorDetailBody {
  detail?: {
    code?: number
    message?: string
    data?: unknown
    traceId?: string
  }
  code?: number
  message?: string
}

/* -------------------------------------------------------------------------- */
/* 鉴权（§5.2，无信封）                                                          */
/* -------------------------------------------------------------------------- */

export interface LoginRequest {
  username: string
  password: string
  deviceId?: string
}

/** `/auth/login` 与 `/auth/refresh` 直接返回该对象，**无包络**。 */
export interface AuthTokens {
  accessToken: string
  refreshToken: string
  tokenType: string
  expiresIn: number
  userId: string
  deviceId: string
}

export interface RefreshRequest {
  refreshToken: string
}

/* -------------------------------------------------------------------------- */
/* 聊天 / 记忆 / 设置（§5.2）                                                    */
/* -------------------------------------------------------------------------- */

export type ChatRole = 'user' | 'bot' | 'system'

/** 服务端 timeline 记录（`http_api.py::chat_history` / `/chat/history`）。 */
export interface TimelineItem {
  messageId: string
  userId: string
  role: ChatRole
  content: string
  timestamp: number
}

export interface ChatHistoryResponse {
  items: TimelineItem[]
}

export interface UserFact {
  factId: string
  userId: string
  fact: string
  timestamp: number
}

export interface MemoryFactsResponse {
  items: UserFact[]
}

export interface CityResponse {
  /** 未设置时服务端返回 `auto_ip`。 */
  city: string
}

/** REST 降级通道请求（FR-CHAT-9）。 */
export interface RestChatRequest {
  requestId?: string
  conversationId?: string
  message: string
  stream?: boolean
  traceId?: string
}

export interface RestChatResponse {
  conversationId: string
  reply: string
  model?: string
  usage?: unknown
  debounceWindowSec?: number
  timerInstruction?: TimerInstruction | null
}

/* -------------------------------------------------------------------------- */
/* WebSocket：客户端 → 服务端（§5.3.1）                                          */
/* -------------------------------------------------------------------------- */

export interface ChatImagePayload {
  mimeType: 'image/jpeg' | 'image/png'
  dataBase64: string
  /** 仅客户端本地字段，服务端忽略（§5.3.1）。 */
  localUri?: string
}

export interface ChatMessagePayload {
  /** 客户端本地字段，服务端忽略；但照常发送以保持语义完整。 */
  messageType: 'text' | 'image'
  content?: string
  images?: ChatImagePayload[]
  /** 客户端本地字段，服务端忽略。 */
  timestamp?: number
}

export interface WsPingFrame {
  type: 'ping'
  payload: { timestamp: number }
}

/**
 * ⚠️ 实测契约补充：`ws_api.py` 用 `message.get("requestId")` 读取**顶层** `requestId`
 * （`request_id = message.get("requestId") or str(uuid.uuid4())`）。
 * 因此 `requestId` 必须放在**顶层**，否则多设备回声与幂等去重全部失效。
 * PRD §5.3.1 的表格只列了 payload，此处按后端源码实现。
 */
export interface WsChatMessageFrame {
  type: 'chat.message'
  requestId: string
  payload: ChatMessagePayload
}

export type WsClientFrame = WsPingFrame | WsChatMessageFrame

/* -------------------------------------------------------------------------- */
/* WebSocket：服务端 → 客户端（§5.3.2、§11.1）                                   */
/* -------------------------------------------------------------------------- */

export interface TimerInstruction {
  /** `HH:MM` */
  target: string
  text: string
}

export interface ChatTypingStage {
  typing: boolean
  /** `vision` / `generating` / `interaction` / `interaction_merge`（§5.3.2）。 */
  stage?: 'vision' | 'generating' | 'interaction' | 'interaction_merge'
}

export type MessageKind = 'greeting' | 'interaction' | 'interaction_failed'

export interface ChatReplyStreamPayload {
  delta: string
  done: boolean
  /** 仅 `done=true` 时存在。 */
  messageId?: string
  /** 仅 `done=true` 时存在；已由服务端清洗、且 `[[TIMER:...]]` 已被剥离。 */
  finalContent?: string
  /** 仅 `done=true` 时存在。 */
  timestamp?: number
  contentType: 'text' | 'image' | 'mixed'
  modelProvider: string
  /** 服务端已解析完成的提醒指令（FR-REM-1：客户端**不**解析原始 `[[TIMER:...]]`）。 */
  timerInstruction?: TimerInstruction | null
  /** `done` 时批量标记被防抖合并的所有用户消息（FR-CHAT-7）。 */
  requestIds?: string[]
  /** 普通聊天无该字段。 */
  messageKind?: MessageKind
  greetingScenario?: 'morning' | 'night' | string
  interactionItemId?: string
  interactionItemName?: string
  interactionItemIcon?: string
  interactionFailed?: boolean
}

/** 服务端错误码全集（§5.3.3）+ 客户端自有错误码。 */
export type ServerErrorCode =
  | 'INVALID_JSON'
  | 'UNKNOWN_TYPE'
  | 'EMPTY_MESSAGE'
  | 'VISION_IMAGE_COUNT_EXCEEDED'
  | 'VISION_INVALID_MIME'
  | 'VISION_INVALID_BASE64'
  | 'VISION_IMAGE_TOO_LARGE'
  | 'GEMINI_NOT_CONFIGURED'
  | 'AI_TIMEOUT'
  | 'INTERNAL_ERROR'

export type ClientErrorCode = 'SEND_FAILED' | 'TIMEOUT' | 'CONNECTION_LOST'
export type AppErrorCode = ServerErrorCode | ClientErrorCode

export interface BotErrorPayload {
  errorCode: AppErrorCode | string
  message: string
  requestIds: string[]
  timestamp: number
}

/* ---- 积分 / 等级 / 补签卡 WS 事件（§11.1，共 5 个） ---- */

export interface PointsChangedPayload {
  ledgerId: number
  reasonCode: PointsReasonCode | string
  changeAmount: number
  balanceAfter: number
  balance: number
  relatedItemId: string | null
  businessDate: string
  timestamp: number
}

/** `changeType` 三态（§7.3 / EDGE-11）。 */
export type LevelChangeType = 'UPGRADE' | 'RESTORE' | 'RESET'

export interface LevelChangedPayload {
  levelCode: string
  levelName: string
  prevLevelCode: string | null
  continuousDays: number
  changeType: LevelChangeType | string
  changeSource: string
  highestLevelCode: string | null
  gapDays: number
  breakDeadlineDate: string | null
  nextLevelCode: string | null
  nextLevelName: string | null
  daysToNextLevel: number | null
  timestamp: number
}

export interface StreakWarningPayload {
  levelCode: string
  levelName: string
  continuousDays: number
  gapDays: number
  remainingDays: number
  deadlineDate: string
  timestamp: number
}

export interface MakeupCardChangedPayload {
  reason: 'MONTHLY_GRANT' | 'USED' | string
  available: number
  used: number
  totalGranted: number
  maxAvailable: number
  lastGrantedMonth: string | null
  timestamp: number
}

export interface PointsSnapshotPayload {
  userId: string
  balance: number
  timestamp: number
}

/* ---- 服务端下行帧联合类型 ---- */

/** `pong` 是**顶层** `{type, timestamp}`，**没有 payload 包装**（§5.3.2）。 */
export interface WsPongFrame {
  type: 'pong'
  timestamp: number
}

export interface WsEchoFrame {
  type: 'chat.message.echo'
  requestId: string
  payload: {
    content: string
    imageCount: number
    timestamp: number
    originDeviceId: string
  }
}

export interface WsQueuedFrame {
  type: 'chat.queued'
  requestId: string
  payload: { debounceWindowSec: number }
}

export interface WsTypingFrame {
  type: 'chat.typing'
  payload: ChatTypingStage
}

export interface WsReplyStreamFrame {
  type: 'chat.reply.stream'
  /** 被防抖合并时取合并后的首个 id。 */
  requestId: string
  payload: ChatReplyStreamPayload
}

export interface WsBotErrorFrame {
  type: 'bot.error'
  requestId: string | null
  payload: BotErrorPayload
}

export interface WsMemoryFactFrame {
  type: 'memory.fact.created'
  payload: UserFact
}

export interface WsAuthExpiredFrame {
  type: 'auth.expired'
  payload: { reason: string; timestamp: number }
}

export interface WsPointsChangedFrame {
  type: 'points.changed'
  payload: PointsChangedPayload
}
export interface WsLevelChangedFrame {
  type: 'level.changed'
  payload: LevelChangedPayload
}
export interface WsStreakWarningFrame {
  type: 'streak.warning'
  payload: StreakWarningPayload
}
export interface WsMakeupCardChangedFrame {
  type: 'makeup_card.changed'
  payload: MakeupCardChangedPayload
}
export interface WsPointsSnapshotFrame {
  type: 'points.snapshot'
  payload: PointsSnapshotPayload
}

/** NFR-12：解析时必须忽略未知 `type` 与未知字段，不得抛错。 */
export type WsServerFrame =
  | WsPongFrame
  | WsEchoFrame
  | WsQueuedFrame
  | WsTypingFrame
  | WsReplyStreamFrame
  | WsBotErrorFrame
  | WsMemoryFactFrame
  | WsAuthExpiredFrame
  | WsPointsChangedFrame
  | WsLevelChangedFrame
  | WsStreakWarningFrame
  | WsMakeupCardChangedFrame
  | WsPointsSnapshotFrame

/* -------------------------------------------------------------------------- */
/* 积分 / 等级 / 互动（§7.0，挂在 ws_api :8001）                                  */
/* -------------------------------------------------------------------------- */

export interface InteractionItem {
  id: string
  name: string
  /** emoji 字符，当前即有值（FR-INT-9）。 */
  icon: string
  /** 图片地址，**当前为空串**，待美术资源接入（FR-INT-9 / EDGE-L23）。 */
  iconUrl: string
  costPoints: number
  sortOrder: number
  /** 置灰依据**必须**用该字段，不得用本地余额自算（FR-INT-4）。 */
  affordable: boolean
}

export interface InteractionItemsData {
  balance: number
  /** 服务端不设每日上限，恒为 null（FR-INT-10）。 */
  dailyLimit: number | null
  items: InteractionItem[]
}

export interface InteractionSendRequest {
  itemId: string
  /** 幂等键，重试必须复用（FR-INT-11）。 */
  requestId: string
  /** 可选附言（§7.1a）。 */
  text?: string
}

export interface InteractionSendData {
  success: boolean
  itemId: string
  requestId: string
  balance: number
  charged: number
  refunded: boolean
  duplicate: boolean
  item: {
    id: string
    name: string
    icon: string
    costPoints: number
  }
  /** 兜底通道；主通道是 WS `chat.reply.stream`（§2.2 契约）。 */
  reply?: string
  messageId?: string
  timerInstruction?: TimerInstruction | null
  /** 被摘走并合进本次回复的防抖缓冲消息（§7.1a、EDGE-L22）。 */
  mergedCount?: number
  mergedRequestIds?: string[]
  /** `40204` 时存在：已退款并给出的兜底文案。 */
  fallbackText?: string
}

export interface PointsBalanceData {
  balance: number
  updatedAt: number
}

/** 等级状态对象（`/points/overview` 的 `level`、`/level/status`）。 */
export interface LevelStatus {
  levelCode: string
  /** `levelCode=NONE` 时为空字符串 + `isDefaultLevel=true`（FR-LV-3 / EDGE-7）。 */
  levelName: string
  prevLevelCode: string | null
  continuousDays: number
  changeType: LevelChangeType | string | null
  changeSource: string | null
  highestLevelCode: string | null
  /** 服务端业务时区的日期字符串 `YYYY-MM-DD`（FR-PROG-3：不得本地推算）。 */
  lastValidDate: string | null
  gapDays: number
  breakDeadlineDate: string | null
  nextLevelCode: string | null
  nextLevelName: string | null
  nextLevelThresholdDays: number | null
  daysToNextLevel: number | null
  levelUpdatedAt: number | null
  isDefaultLevel: boolean
  streakDates?: string[]
  availableMakeupCards?: number
}

/** 补签卡汇总（camelCase）。 */
export interface MakeupCardSummary {
  available: number
  used: number
  totalGranted: number
  /** 上限，服务端下发，**不得硬编码**（FR-MC-1）。 */
  maxAvailable: number
  /** 每月发放张数，服务端下发（FR-MC-1）。 */
  monthlyGrant: number
  lastGrantedMonth: string | null
  currentMonthGranted: boolean
  atLimit: boolean
}

/** 首屏聚合（FR-PT-1 / FR-PROG-5：Profile 页首选，1 次请求拿全量）。 */
export interface PointsOverviewData {
  balance: number
  balanceUpdatedAt: number
  level: LevelStatus
  makeupCard: MakeupCardSummary
}

/* ---- snake_case DTO（§7.0 字段名契约） ---- */

export interface LevelConfigEntry {
  level_code: string
  level_name: string
  threshold_days: number
  sort_order: number
}

export interface LevelConfigData {
  levels: LevelConfigEntry[]
  defaultLevelCode: string
  defaultLevelName: string
  makeupCardMax: number
  breakGapDays: number
  warningGapDays: number[]
}

export interface PointsLedgerEntry {
  id: number
  user_id: string
  change_amount: number
  reason_code: PointsReasonCode | string
  balance_after: number
  related_item_id: string | null
  idempotency_key: string
  created_at: number
  business_date: string
}

export interface PagedData<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
  hasMore: boolean
}

export type PointsHistoryData = PagedData<PointsLedgerEntry>

export interface MakeupCardRecord {
  id: number
  user_id: string
  granted_month: string
  status: 'AVAILABLE' | 'USED' | string
  used_for_date: string | null
  used_at: number | null
  created_at: number
}

export type MakeupCardHistoryData = PagedData<MakeupCardRecord>

export interface MakeupCandidate {
  /** `YYYY-MM-DD`，服务端业务时区。 */
  date: string
  daysAgo: number
}

export interface MakeupCandidatesData {
  items: MakeupCandidate[]
  total: number
  firstActivityDate: string | null
  available: number
}

export interface MakeupCardUseRequest {
  targetDate: string
}

export interface MakeupCardUseData {
  success: boolean
  availableCards: number
  targetDate: string
  card: MakeupCardRecord | null
  /** 依据 `level.changeType` 决定反馈样式（FR-MC-5）。 */
  level: LevelStatus | null
}

/** 流水事由本地化键（FR-PT-3）。 */
export type PointsReasonCode =
  | 'DAILY_FIRST_CHAT'
  | 'STREAK_3_DAY'
  | 'ANNIVERSARY'
  | 'ITEM_SEND'
  | 'ITEM_REFUND'
  | 'ADMIN_ADJUST'
  | string

/* -------------------------------------------------------------------------- */
/* 本地模型（§9）                                                                */
/* -------------------------------------------------------------------------- */

export type MessageStatus = 'sending' | 'sent' | 'received' | 'streaming' | 'error'

export interface ChatMessage {
  messageId: string
  sessionId: string
  role: ChatRole
  messageType: 'text' | 'image'
  contentType: 'text' | 'image' | 'mixed'
  modelProvider: string | null
  content: string
  status: MessageStatus
  timestamp: number
  errorCode: string | null
  /** 互动回复来源标记（FR-INT-6），仅内存/展示用。 */
  interactionItemIcon?: string | null
  interactionItemName?: string | null
  messageKind?: MessageKind | null
  /** 该消息附带的图片附件（渲染时按 messageId 关联）。 */
  attachments?: ChatAttachment[]
}

export interface ChatAttachment {
  attachmentId: string
  messageId: string
  sessionId: string
  mimeType: string
  localPath: string
  fileSize: number
  width: number | null
  height: number | null
  uploadState: string
  timestamp: number
}

export interface BotNotification {
  notificationId: string
  errorCode: string
  message: string
  isRead: boolean
  timestamp: number
}

export interface ReminderRecord {
  reminderId: string
  targetTime: string
  fireAt: number
  text: string
  status: 'pending' | 'fired' | 'cancelled'
  createdAt: number
}

/** 积分/等级离线缓存（§9.6，camelCase 语义）。 */
export interface UserProgressCache {
  userId: string
  balance: number
  balanceUpdatedAt: number
  levelCode: string
  levelName: string
  prevLevelCode: string | null
  highestLevelCode: string | null
  continuousDays: number
  nextLevelCode: string | null
  nextLevelName: string | null
  nextLevelThresholdDays: number | null
  daysToNextLevel: number | null
  lastValidDate: string | null
  gapDays: number
  breakDeadlineDate: string | null
  isDefaultLevel: boolean
  levelUpdatedAt: number | null
  availableMakeupCards: number
  syncedAt: number
}

/** 应用设置（渲染进程可读；敏感字段不下发）。 */
export interface AppSettings {
  apiBaseUrl: string
  wsBaseUrl: string
  theme: 'system' | 'light' | 'dark'
  city: string
  autostart: boolean
  autostartHidden: boolean
  closeBehavior: 'tray' | 'quit'
  globalShortcut: string
  globalShortcutEnabled: boolean
  sendKey: 'enter' | 'ctrl+enter'
  notifications: {
    chat: boolean
    greeting: boolean
    reminder: boolean
    error: boolean
    progress: boolean
  }
  doNotDisturb: {
    enabled: boolean
    /** `HH:MM` */
    start: string
    /** `HH:MM` */
    end: string
  }
  window: {
    width: number
    height: number
    x: number | null
    y: number | null
  }
  lastUsername: string
  allowPlaintextCredentials: boolean
}

/**
 * 设置补丁类型。
 *
 * `Partial<AppSettings>` 无法表达「只改一个嵌套字段」（例如只关掉 `notifications.progress`），
 * 会强制调用方把整个 `notifications` 对象补齐 —— 既啰嗦又容易漏字段。
 * 主进程的 `mergeSettings` 本就按字段合并，这里把类型对齐到实现。
 */
export type SettingsPatch = Omit<Partial<AppSettings>, 'notifications' | 'doNotDisturb' | 'window'> & {
  notifications?: Partial<AppSettings['notifications']>
  doNotDisturb?: Partial<AppSettings['doNotDisturb']>
  window?: Partial<AppSettings['window']>
}

export interface ConnectionState {
  status: 'unauthenticated' | 'connecting' | 'connected' | 'reconnecting' | 'refreshing' | 'degraded' | 'disconnected'
  attempt: number
  lastError: string | null
  degraded: boolean
}
