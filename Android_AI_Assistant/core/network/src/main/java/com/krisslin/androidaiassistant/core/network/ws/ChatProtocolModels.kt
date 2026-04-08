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
    val timestamp: Long
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
    val timerInstruction: TimerInstruction? = null,
    val requestIds: List<String>? = null
)

data class BotErrorPayload(
    val errorCode: String,
    val message: String,
    val requestIds: List<String> = emptyList(),
    val timestamp: Long
)

data class MemoryFactPayload(
    val factId: String,
    val userId: String,
    val fact: String,
    val timestamp: Long
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
    data object Typing : IncomingMessage
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

            "chat.typing" -> IncomingMessage.Typing
            "auth.expired" -> IncomingMessage.AuthExpired
            else -> IncomingMessage.Unknown(type, raw)
        }
    }
}
