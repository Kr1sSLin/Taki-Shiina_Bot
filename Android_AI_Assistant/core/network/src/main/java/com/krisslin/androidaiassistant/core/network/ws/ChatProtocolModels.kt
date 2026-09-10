package com.krisslin.androidaiassistant.core.network.ws

import com.google.gson.Gson

data class ChatMessageRequest(
    val type: String = "chat.message",
    val requestId: String,
    val payload: ChatMessagePayload
)

data class ChatMessagePayload(
    val messageType: String,
    val content: String? = null,
    val imageUrl: String? = null,
    val images: List<ChatImagePayload>? = null,
    val timestamp: Long
)

data class ChatImagePayload(
    val mimeType: String,
    val dataBase64: String,
    val localUri: String? = null
)

data class ReplyPayload(
    val messageId: String,
    val content: String,
    val weatherAttached: Boolean = false,
    val timestamp: Long
)

data class TimerInstruction(
    val target: String,
    val text: String
)

data class ReplyStreamPayload(
    val delta: String = "",
    val done: Boolean = false,
    val messageId: String? = null,
    val finalContent: String? = null,
    val contentType: String? = null,
    val modelProvider: String? = null,
    val timerInstruction: TimerInstruction? = null,
    val requestIds: List<String>? = null,
    val timestamp: Long? = null,
    val messageKind: String? = null,
    val greetingScenario: String? = null,
    // 互动礼物（PRD FR-8）：复用同一聊天通道，用 messageKind=interaction 区分
    val interactionItemId: String? = null,
    val interactionItemName: String? = null,
    val interactionItemIcon: String? = null,
    val interactionFailed: Boolean? = null
)

// ==================== 互动积分 · 等级体系事件（PRD §8 / 契约文档 3.2~3.5） ====================

/** 积分变动（EDGE-4：每条规则独立事件，不合并金额）。 */
data class PointsChangedPayload(
    val ledgerId: Long? = null,
    val reasonCode: String? = null,
    val changeAmount: Int = 0,
    val balanceAfter: Int = 0,
    val balance: Int? = null,
    val relatedItemId: String? = null,
    val businessDate: String? = null,
    val note: String? = null,
    val timestamp: Long = 0
)

/**
 * 等级变化。
 * changeType：UPGRADE（首次达成，庆祝）/ RESTORE（补签回溯挽回，克制文案）/ RESET（断签回落）。
 */
data class LevelChangedPayload(
    val levelCode: String = "NONE",
    val levelName: String = "",
    val prevLevelCode: String? = null,
    val continuousDays: Int = 0,
    val changeType: String? = null,
    val changeSource: String? = null,
    val highestLevelCode: String? = null,
    val lastValidDate: String? = null,
    val gapDays: Int? = null,
    val breakDeadlineDate: String? = null,
    val nextLevelCode: String? = null,
    val nextLevelName: String? = null,
    val nextLevelThresholdDays: Int? = null,
    val daysToNextLevel: Int? = null,
    val levelUpdatedAt: Long? = null,
    val timestamp: Long = 0
)

/** 断签提前提醒（FR-18：连续 3～4 天未对话）。 */
data class StreakWarningPayload(
    val levelCode: String = "NONE",
    val levelName: String = "",
    val continuousDays: Int = 0,
    val gapDays: Int = 0,
    val remainingDays: Int = 0,
    val deadlineDate: String? = null,
    val timestamp: Long = 0
)

/** 补签卡库存变动（reason：MONTHLY_GRANT / USED）。 */
data class MakeupCardChangedPayload(
    val reason: String? = null,
    val available: Int = 0,
    val used: Int = 0,
    val totalGranted: Int = 0,
    val maxAvailable: Int = 12,
    val lastGrantedMonth: String? = null,
    val timestamp: Long = 0
)

data class BotErrorPayload(
    val errorCode: String,
    val message: String,
    val requestIds: List<String> = emptyList(),
    val timestamp: Long
)

data class UserEchoPayload(
    val content: String = "",
    val imageCount: Int = 0,
    val originDeviceId: String? = null,
    val timestamp: Long
)

data class MemoryFactPayload(
    val factId: String,
    val userId: String,
    val fact: String,
    val timestamp: Long
)

data class TypingPayload(
    val typing: Boolean = true,
    val stage: String? = null
)

data class IncomingEnvelope<T>(
    val type: String,
    val requestId: String? = null,
    val payload: T
)

sealed interface IncomingMessage {
    data class Reply(val requestId: String?, val payload: ReplyPayload) : IncomingMessage
    data class ReplyStream(val requestId: String?, val payload: ReplyStreamPayload) : IncomingMessage
    data class BotError(val requestId: String?, val payload: BotErrorPayload) : IncomingMessage
    data class MemoryFactCreated(val payload: MemoryFactPayload) : IncomingMessage
    data class UserEcho(val requestId: String?, val payload: UserEchoPayload) : IncomingMessage
    data class Typing(val payload: TypingPayload) : IncomingMessage
    data class PointsChanged(val payload: PointsChangedPayload) : IncomingMessage
    data class LevelChanged(val payload: LevelChangedPayload) : IncomingMessage
    data class StreakWarning(val payload: StreakWarningPayload) : IncomingMessage
    data class MakeupCardChanged(val payload: MakeupCardChangedPayload) : IncomingMessage
    data object AuthExpired : IncomingMessage
    data class Unknown(val type: String, val raw: String) : IncomingMessage
}

class IncomingMessageParser(
    private val gson: Gson
) {
    fun parse(raw: String): IncomingMessage {
        val envelope = gson.fromJson(raw, Map::class.java)
        val type = envelope["type"]?.toString() ?: return IncomingMessage.Unknown("missing", raw)
        return when (type) {
            "chat.reply" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.Reply(
                        requestId = it.requestId,
                        payload = gson.fromJson(payloadJson, ReplyPayload::class.java)
                    )
                }

            "chat.reply.stream" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.ReplyStream(
                        requestId = it.requestId,
                        payload = gson.fromJson(payloadJson, ReplyStreamPayload::class.java)
                    )
                }

            "bot.error" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.BotError(
                        requestId = it.requestId,
                        payload = gson.fromJson(payloadJson, BotErrorPayload::class.java)
                    )
                }

            "memory.fact.created" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.MemoryFactCreated(
                        payload = gson.fromJson(payloadJson, MemoryFactPayload::class.java)
                    )
                }

            "chat.message.echo" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.UserEcho(
                        requestId = it.requestId,
                        payload = gson.fromJson(payloadJson, UserEchoPayload::class.java)
                    )
                }

            "chat.typing" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.Typing(
                        payload = gson.fromJson(payloadJson, TypingPayload::class.java)
                    )
                }
            "points.changed" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.PointsChanged(
                        payload = gson.fromJson(payloadJson, PointsChangedPayload::class.java)
                    )
                }
            "level.changed" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.LevelChanged(
                        payload = gson.fromJson(payloadJson, LevelChangedPayload::class.java)
                    )
                }
            "streak.warning" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.StreakWarning(
                        payload = gson.fromJson(payloadJson, StreakWarningPayload::class.java)
                    )
                }
            "makeup_card.changed" -> gson.fromJson(raw, IncomingEnvelope::class.java)
                .let {
                    val payloadJson = gson.toJsonTree(it.payload)
                    IncomingMessage.MakeupCardChanged(
                        payload = gson.fromJson(payloadJson, MakeupCardChangedPayload::class.java)
                    )
                }
            "auth.expired" -> IncomingMessage.AuthExpired
            else -> IncomingMessage.Unknown(type, raw)
        }
    }
}
