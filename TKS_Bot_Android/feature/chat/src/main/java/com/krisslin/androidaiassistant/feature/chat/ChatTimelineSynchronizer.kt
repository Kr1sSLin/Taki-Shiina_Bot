package com.krisslin.androidaiassistant.feature.chat

import android.util.Log
import com.google.gson.JsonArray
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.SyncOutcome
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage

internal data class HistorySyncResult(val success: Boolean, val inserted: Int = 0, val updated: Int = 0)

/** Owns incremental timeline/fact cursors and Room reconciliation; it has no ViewModel or UI access. */
internal class ChatTimelineSynchronizer(
    private val chatApi: ChatApi,
    private val chatRepository: ChatRepository,
    private val userFactRepository: UserFactRepository,
    private val sessionId: String
) {
    private var historyCursorMs = 0L
    private var factCursorMs = 0L

    suspend fun initializeFactCursor() {
        factCursorMs = userFactRepository.getLatestTimestamp()
    }

    fun advanceHistoryCursor(timestamp: Long) {
        historyCursorMs = timestamp
    }

    suspend fun syncHistory(force: Boolean): HistorySyncResult = runCatching {
        val response = chatApi.history(since = if (force) 0L else historyCursorMs, limit = 300)
        val items = response.getAsJsonObject("data")?.getAsJsonArray("items") ?: JsonArray()
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
                    timestamp = item.timestamp
                )
            ) {
                SyncOutcome.INSERTED -> inserted++
                SyncOutcome.UPDATED -> updated++
                SyncOutcome.UNCHANGED -> Unit
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
        val payload = message.payload
        userFactRepository.upsertAll(
            listOf(UserFactEntity(payload.factId, payload.userId, payload.fact, payload.timestamp))
        )
        if (payload.timestamp > factCursorMs) factCursorMs = payload.timestamp
    }

    suspend fun syncMemoryFacts() {
        runCatching {
            chatApi.memoryFacts(since = factCursorMs, limit = 200)
        }.onSuccess { response ->
            val items = response.getAsJsonObject("data")?.getAsJsonArray("items") ?: JsonArray()
            val facts = buildList {
                for (element in items) {
                    val item = element.asObjectOrNull() ?: continue
                    add(
                        UserFactEntity(
                            factId = item.stringOrNull("factId") ?: continue,
                            userId = item.stringOrNull("userId") ?: continue,
                            fact = item.stringOrNull("fact") ?: continue,
                            timestamp = item.longOrNull("timestamp") ?: continue
                        )
                    )
                }
            }
            if (facts.isNotEmpty()) {
                userFactRepository.upsertAll(facts)
                factCursorMs = maxOf(factCursorMs, facts.maxOf { it.timestamp })
            }
        }.onFailure { error ->
            Log.w("ChatTimelineSync", "memory facts sync failed: ${error.message}", error)
        }
    }
}
