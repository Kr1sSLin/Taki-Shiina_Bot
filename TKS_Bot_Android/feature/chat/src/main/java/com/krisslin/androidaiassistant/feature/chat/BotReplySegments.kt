package com.krisslin.androidaiassistant.feature.chat

/** A persisted bot bubble derived deterministically from one server reply. */
data class BotReplySegment(
    val messageId: String,
    val content: String,
    val timestamp: Long
)

/**
 * Owns the cross-transport reply identity rule. WS, HTTP fallback and history sync must all use
 * this function so Room's primary key provides the third and final layer of deduplication.
 */
object BotReplySegments {
    fun split(content: String): List<String> = content
        .split('\n')
        .map { it.trim() }
        .filter { it.isNotBlank() }

    fun create(messageId: String, content: String, timestamp: Long): List<BotReplySegment> {
        val parts = split(content)
        return if (parts.isEmpty()) {
            listOf(BotReplySegment(messageId, content, timestamp))
        } else {
            parts.mapIndexed { index, part ->
                BotReplySegment(
                    messageId = "${messageId}_$index",
                    content = part,
                    timestamp = timestamp + index
                )
            }
        }
    }
}

/** Bounded in-memory guard used to suppress the HTTP fallback after the same WS reply arrived. */
internal class DeliveredReplyTracker(private val maxEntries: Int = 200) {
    private val ids = LinkedHashSet<String>()

    @Synchronized
    fun mark(messageId: String) {
        if (ids.size > maxEntries) ids.clear()
        ids.add(messageId)
    }

    @Synchronized
    fun contains(messageId: String): Boolean = messageId in ids
}
