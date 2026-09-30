package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonObject

data class TimelineHistoryItem(
    val messageId: String,
    val role: String,
    val content: String,
    val timestamp: Long,
    val sourceTimestamp: Long = timestamp
)

/** Pure parsing/normalization for incremental history reconciliation. */
object TimelineHistoryNormalizer {
    fun normalize(items: JsonArray): List<TimelineHistoryItem> = buildList {
        for (element in items) {
            val item = element.asObjectOrNull() ?: continue
            val messageId = item.stringOrNull("messageId") ?: continue
            val role = item.stringOrNull("role") ?: continue
            val content = item.stringOrNull("content") ?: continue
            if (content.isBlank()) continue
            val timestamp = item.longOrNull("timestamp") ?: continue

            if (role == "user") {
                add(TimelineHistoryItem(messageId, role, content, timestamp))
            } else {
                BotReplySegments.create(messageId, content, timestamp).forEach { segment ->
                    add(
                        TimelineHistoryItem(
                            messageId = segment.messageId,
                            role = role,
                            content = segment.content,
                            timestamp = segment.timestamp,
                            sourceTimestamp = timestamp
                        )
                    )
                }
            }
        }
    }
}

internal fun JsonObject.stringOrNull(name: String): String? {
    val value = get(name) ?: return null
    return if (value.isJsonPrimitive) runCatching { value.asString }.getOrNull() else null
}

internal fun JsonObject.longOrNull(name: String): Long? {
    val value = get(name) ?: return null
    return if (value.isJsonPrimitive) runCatching { value.asString.toLongOrNull() }.getOrNull() else null
}

internal fun JsonElement.asObjectOrNull(): JsonObject? = if (isJsonObject) asJsonObject else null
