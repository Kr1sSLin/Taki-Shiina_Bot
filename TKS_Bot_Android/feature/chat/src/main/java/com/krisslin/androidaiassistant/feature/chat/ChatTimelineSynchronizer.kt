package com.krisslin.androidaiassistant.feature.chat

import android.util.Log
import com.google.gson.JsonArray
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.ModelProvider
import com.krisslin.androidaiassistant.core.database.repository.SyncOutcome
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage

internal data class HistorySyncResult(val success: Boolean, val inserted: Int = 0, val updated: Int = 0)

/** Owns incremental timeline/fact cursors and Room reconciliation; it has no ViewModel or UI access. */
internal class ChatTimelineSynchronizer(
    private val chatApi: ChatApi,
    private val chatRepository: ChatRepository,
    private val userFactRepository: UserFactRepository,
    private val authRepository: AuthRepository,
    private val sessionId: String,
    private val imageAttachmentManager: ChatImageAttachmentManager
) {
    private var historyCursorMs = 0L

    fun advanceHistoryCursor(timestamp: Long) {
        historyCursorMs = timestamp
    }

    suspend fun syncHistory(force: Boolean): HistorySyncResult = runCatching {
        val response = chatApi.history(since = if (force) 0L else historyCursorMs, limit = 300)
        val items = response.getAsJsonObject("data")?.getAsJsonArray("items") ?: JsonArray()
        imageAttachmentManager.retryPendingHistoryImages()
        var inserted = 0
        var updated = 0
        val normalized = TimelineHistoryNormalizer.normalize(items)
        for (item in normalized) {
            val role = if (item.role == "user") MessageRole.USER else MessageRole.BOT
            when (
                chatRepository.saveExternalMessage(
                    messageId = item.messageId,
                    sessionId = sessionId,
                    role = role,
                    content = item.content,
                    timestamp = item.timestamp,
                    contentType = HistoryImageRestorePolicy.contentType(item.content, item.imageCount),
                    modelProvider = if (item.imageCount > 0) ModelProvider.GEMINI else ModelProvider.DEEPSEEK
                )
            ) {
                SyncOutcome.INSERTED -> inserted++
                SyncOutcome.UPDATED -> updated++
                SyncOutcome.UNCHANGED -> Unit
            }
            if (role == MessageRole.USER && item.imageCount > 0) {
                imageAttachmentManager.restoreHistoryImages(
                    requestId = item.requestId ?: item.messageId,
                    messageId = item.messageId,
                    imageCount = item.imageCount
                )
            }
        }
        for (element in response.getAsJsonObject("data")?.getAsJsonArray("operations") ?: JsonArray()) {
            val operation = element.asJsonObject
            if (operation.get("status")?.asString != "failed") continue
            for (source in TimelineHistoryNormalizer.normalize(operation.getAsJsonArray("sources") ?: JsonArray())) {
                if (chatRepository.getMessage(source.messageId) == null) {
                    if (!force && source.sourceTimestamp <= historyCursorMs) continue
                    chatRepository.saveExternalMessage(source.messageId, sessionId, MessageRole.USER, source.content, source.timestamp,
                        HistoryImageRestorePolicy.contentType(source.content, source.imageCount), ModelProvider.DEEPSEEK)
                }
                chatRepository.markMessageError(source.messageId, "RESEND_NEW_REQUEST")
                if (source.imageCount > 0) imageAttachmentManager.restoreHistoryImages(source.requestId ?: source.messageId, source.messageId, source.imageCount)
            }
            for (identity in operation.getAsJsonArray("requestIds") ?: JsonArray()) {
                val requestId = identity.asString
                chatRepository.markMessageError(requestId, "RESEND_NEW_REQUEST")
                chatRepository.deleteMessage("pending_$requestId")
            }
        }
        // Commit the cursor only after every Room write succeeds. Display timestamps
        // contain synthetic segment offsets; they must never become server cursors.
        // Re-read the boundary millisecond because the API uses strict `> since`.
        normalized.maxOfOrNull { it.sourceTimestamp }?.let {
            historyCursorMs = maxOf(historyCursorMs, (it - 1).coerceAtLeast(0L))
        }
        Log.i("ChatTimelineSync", "history sync ok: items=${items.size()} inserted=$inserted healed=$updated force=$force")
        HistorySyncResult(true, inserted, updated)
    }.getOrElse { error ->
        Log.w("ChatTimelineSync", "history sync failed: ${error.message}", error)
        HistorySyncResult(false)
    }

    suspend fun onMemoryFactCreated(message: IncomingMessage.MemoryFactCreated) {
        // WebSocket is a wake-up hint; only the authoritative event page may move the cursor.
        syncMemoryFacts()
    }

    suspend fun syncMemoryFacts() {
        runCatching {
            val userId = authRepository.getUserId() ?: return@runCatching
            var cursor = userFactRepository.cursor(userId)
            repeat(100) {
                val response = chatApi.memoryFacts(cursor = cursor, limit = 200, userId = userId)
                val data = response.getAsJsonObject("data") ?: error("Missing fact page")
                val items = data.getAsJsonArray("items") ?: JsonArray()
                val nextCursor = data.get("nextCursor")?.asLong ?: error("Missing fact cursor")
                val events = buildList {
                    for (element in items) {
                        val item = element.asObjectOrNull() ?: continue
                        val eventId = item.stringOrNull("eventId") ?: continue
                        add(eventId to UserFactEntity(
                            factId = item.stringOrNull("factId") ?: continue,
                            userId = item.stringOrNull("userId") ?: continue,
                            fact = item.stringOrNull("fact").orEmpty(),
                            timestamp = item.longOrNull("timestamp") ?: 0L,
                            revision = item.longOrNull("revision") ?: 0L,
                            revisionId = item.stringOrNull("revisionId"),
                            permissionVersion = item.longOrNull("permissionVersion") ?: 0L,
                            enabled = item.get("enabled")?.asBoolean ?: false,
                            stage = item.stringOrNull("stage") ?: "candidate",
                            truthStatus = item.stringOrNull("truthStatus") ?: "pending",
                            score = item.get("score")?.asDouble ?: 0.0,
                            sourceMessageId = item.stringOrNull("sourceMessageId"),
                            temporalValidity = item.get("temporalValidity")?.toString()
                        ))
                    }
                }
                userFactRepository.applyPage(userId, events, nextCursor)
                if (data.get("hasMore")?.asBoolean != true) return@runCatching
                if (nextCursor <= cursor) error("Fact cursor did not advance")
                cursor = nextCursor
            }
            error("Fact pagination exceeded 100 pages")
        }.onFailure { error ->
            Log.w("ChatTimelineSync", "memory facts sync failed: ${error.message}", error)
        }
    }
}
