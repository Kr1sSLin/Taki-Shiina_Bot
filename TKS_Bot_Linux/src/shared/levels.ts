/**
 * 等级视觉规范（FR-LV-2）与全局常量。
 *
 * 配色来源：`视觉资产/熊猫图像.txt`（与本文件默认值、后端 `/level/config` 默认阈值一致）。
 * ⚠️ FR-LV-2：等级阈值**以服务端 `GET /level/config` 返回为准**（运营可后台改），
 *    本表仅为**设计兜底与配色映射**，不得用于业务判定。
 */

export interface LevelVisual {
  code: string
  /** 兜底称号（服务端 `level_name` 优先）。 */
  fallbackName: string
  fallbackNameEn: string
  emoji: string
  /** CSS 渐变色标。 */
  gradient: [string, string]
  /** 主色（用于进度条、徽章描边）。 */
  accent: string
  /** 兜底阈值天数（服务端 `threshold_days` 优先）。 */
  fallbackThresholdDays: number
  /** 「传奇熊猫」使用七彩动态特效（FR-LV-4）。 */
  animated?: boolean
}

export const LEVEL_VISUALS: LevelVisual[] = [
  {
    code: 'PANDA_LV1',
    fallbackName: '初生熊猫',
    fallbackNameEn: 'Newborn Panda',
    emoji: '🥚',
    gradient: ['#D9D9D9', '#BFC3C7'],
    accent: '#B9BDC1',
    fallbackThresholdDays: 3
  },
  {
    code: 'PANDA_LV2',
    fallbackName: '好奇宝宝',
    fallbackNameEn: 'Curious Cub',
    emoji: '🌱',
    gradient: ['#C9E7A8', '#8FCB6B'],
    accent: '#8FCB6B',
    fallbackThresholdDays: 7
  },
  {
    code: 'PANDA_LV3',
    fallbackName: '竹林新秀',
    fallbackNameEn: 'Bamboo Rookie',
    emoji: '🍃',
    gradient: ['#8FDCA0', '#37A65C'],
    accent: '#37A65C',
    fallbackThresholdDays: 15
  },
  {
    code: 'PANDA_LV4',
    fallbackName: '黑白骑士',
    fallbackNameEn: 'Monochrome Knight',
    emoji: '🌊',
    gradient: ['#3E6FA8', '#1B3B63'],
    accent: '#2C5A8C',
    fallbackThresholdDays: 30
  },
  {
    code: 'PANDA_LV5',
    fallbackName: '功夫大师',
    fallbackNameEn: 'Kung Fu Master',
    emoji: '✨',
    gradient: ['#F7D774', '#D8A32B'],
    accent: '#D8A32B',
    fallbackThresholdDays: 60
  },
  {
    code: 'PANDA_LV6',
    fallbackName: '熊猫长老',
    fallbackNameEn: 'Panda Elder',
    emoji: '💜',
    gradient: ['#B48CE0', '#7A4FBF'],
    accent: '#8B5CD6',
    fallbackThresholdDays: 100
  },
  {
    code: 'PANDA_LV7',
    fallbackName: '传奇熊猫',
    fallbackNameEn: 'Legendary Panda',
    emoji: '🌈',
    gradient: ['#FF6B6B', '#FFD93D'],
    accent: '#FF8A5B',
    fallbackThresholdDays: 200,
    animated: true
  }
]

export const LEVEL_VISUAL_BY_CODE: Record<string, LevelVisual> = Object.fromEntries(
  LEVEL_VISUALS.map((v) => [v.code, v])
)

/** 默认态（`levelCode=NONE`）中性兜底视觉（FR-LV-3：不设专属文案）。 */
export const DEFAULT_LEVEL_VISUAL: LevelVisual = {
  code: 'NONE',
  fallbackName: '刚刚开始',
  fallbackNameEn: 'Just Started',
  emoji: '🐾',
  gradient: ['#CFD4DA', '#A8AFB8'],
  accent: '#A8AFB8',
  fallbackThresholdDays: 0
}

/* -------------------------------------------------------------------------- */
/* 协议与后端行为常量（§5.4）                                                    */
/* -------------------------------------------------------------------------- */

export const PROTOCOL = {
  /** 心跳间隔 25s（§5.3.1，与 Android `OkHttpBotWebSocketClient.kt:90` 一致）。 */
  HEARTBEAT_INTERVAL_MS: 25_000,
  /** 重连退避 `2^n` 秒、上限 60s、最多 15 次（FR-CONN-3）。 */
  RECONNECT_BASE_MS: 1_000,
  RECONNECT_MAX_MS: 60_000,
  RECONNECT_MAX_ATTEMPTS: 15,
  /** 客户端流式超时 150s，每收到 delta 重置（FR-CHAT-8）。 */
  STREAMING_TIMEOUT_MS: 150_000,
  /** 互动请求 HTTP 超时必须 > 60s（FR-INT-8 / §7.1a）。 */
  INTERACTION_TIMEOUT_MS: 90_000,
  /** 常规 REST 超时。 */
  REST_TIMEOUT_MS: 30_000,
  /** 历史同步 limit（FR-SYNC-3）。 */
  SYNC_LIMIT: 300,
  /** 每用户服务端历史上限（仅作提示，客户端不截断本地）。 */
  SERVER_HISTORY_LIMIT: 300,
  /** 图片约束（FR-IMG-2，与服务端 `VISION_*` 一致）。 */
  MAX_IMAGE_COUNT: 3,
  MAX_IMAGE_BYTES: 20 * 1024 * 1024,
  ALLOWED_IMAGE_MIME: ['image/jpeg', 'image/png'] as string[],
  /** 启动时过期提醒处理阈值（FR-REM-4）：超过 30 分钟丢弃。 */
  REMINDER_STALE_MS: 30 * 60 * 1000,
  /** 默认服务地址（§5.1）。 */
  DEFAULT_API_BASE_URL: 'https://takishiinabot.top/api/v1/',
  DEFAULT_WS_BASE_URL: 'wss://takishiinabot.top',
  /** 服务端每日 06:00 插入的分隔标记（FR-SYNC-8）。 */
  HISTORY_SEPARATOR: '──── 新的一天 ────',
  /** 单会话（OQ-5）。 */
  DEFAULT_SESSION_ID: 'default_session',
  /** 默认全局快捷键（FR-DSK-2）。 */
  DEFAULT_GLOBAL_SHORTCUT: 'Control+Alt+T'
} as const

/** WS 关闭码：鉴权失败（§5.3）。 */
export const WS_CLOSE_INVALID_TOKEN = 4001
