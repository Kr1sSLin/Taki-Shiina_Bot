package com.krisslin.androidaiassistant.feature.chat

import android.util.Log
import com.google.gson.JsonArray
import com.google.gson.JsonParser
import com.krisslin.androidaiassistant.core.network.BuildConfig
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import retrofit2.HttpException
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.ModelProvider
import com.krisslin.androidaiassistant.core.database.repository.SyncOutcome
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage

internal data class HistorySyncResult(val success: Boolean, val inserted: Int = 0, val updated: Int = 0, val notice: String? = null)

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

    private val historyMutex = Mutex()

    suspend fun syncHistory(force: Boolean): HistorySyncResult = historyMutex.withLock {
        try {
            val userId = authRepository.getUserId() ?: return@withLock HistorySyncResult(false)
            val scope = "${BuildConfig.API_BASE_URL.trimEnd('/')}|$userId"
            fun checkAccount() { check(authRepository.getUserId() == userId) { "历史同步账户已变化" } }
            var cursor = if (force) "start" else chatRepository.historyState(scope) ?: "start"
            var resets = 0
            var inserted = 0
            var updated = 0
            while (true) {
                checkAccount()
                val response = try {
                    chatApi.history(cursor = cursor, limit = 300)
                } catch (error: HttpException) {
                    val code = runCatching { JsonParser.parseString(error.response()?.errorBody()?.string()).asJsonObject.get("code")?.asInt }.getOrNull()
                    if (code == 40910 && resets++ == 0) { cursor = "start"; continue }
                    throw error
                }
                checkAccount()
                check(response.get("code")?.asInt == 0) { "历史同步响应失败" }
                var data = response.getAsJsonObject("data") ?: error("缺少历史页")
                val limited = data.get("historySyncVersion")?.asInt != 1
                if (limited && !force && historyCursorMs > 0) {
                    data = chatApi.history(since = historyCursorMs, limit = 300).getAsJsonObject("data") ?: error("缺少历史页")
                    checkAccount()
                }
                val next = if (limited) null else data.stringOrNull("nextCursor") ?: error("缺少历史检查点")
                if (!limited) check(data.stringOrNull("datasetGeneration") != null && data.has("hasMore"))
                val normalized = TimelineHistoryNormalizer.normalize(data.getAsJsonArray("items") ?: JsonArray())
                chatRepository.historyTransaction {
                    for (item in normalized) {
                        checkAccount()
                        val sqlKey = "$scope:sql:${item.sourceMessageId}"
                        if (item.historySource == "legacy" && chatRepository.historyState(sqlKey) != null) continue
                        val role = if (item.role == "user") MessageRole.USER else MessageRole.BOT
                        when (chatRepository.saveExternalMessage(item.messageId, sessionId, role, item.content, item.timestamp,
                            HistoryImageRestorePolicy.contentType(item.content, item.imageCount),
                            if (item.imageCount > 0) ModelProvider.GEMINI else ModelProvider.DEEPSEEK,
                            authoritative = item.historySource == "sql")) {
                            SyncOutcome.INSERTED -> inserted++
                            SyncOutcome.UPDATED -> updated++
                            SyncOutcome.UNCHANGED -> Unit
                        }
                        chatRepository.putHistoryState("$scope:seen:${item.messageId}", "1")
                        if (item.historySource == "sql") chatRepository.putHistoryState(sqlKey, "1")
                        if (role == MessageRole.USER && item.imageCount > 0) imageAttachmentManager.restoreHistoryImages(
                            item.requestId ?: item.messageId, item.messageId, item.imageCount, download = false)
                    }
                    for (element in data.getAsJsonArray("operations") ?: JsonArray()) {
                        val operation = element.asJsonObject
                        if (operation.get("status")?.asString != "failed") continue
                        for (source in TimelineHistoryNormalizer.normalize(operation.getAsJsonArray("sources") ?: JsonArray())) {
                            if (chatRepository.getMessage(source.messageId) == null) {
                                if (limited && !force && source.sourceTimestamp <= historyCursorMs) continue
                                chatRepository.saveExternalMessage(source.messageId, sessionId, MessageRole.USER, source.content, source.timestamp,
                                    HistoryImageRestorePolicy.contentType(source.content, source.imageCount), ModelProvider.DEEPSEEK)
                            }
                            chatRepository.markMessageError(source.messageId, "RESEND_NEW_REQUEST")
                            chatRepository.putHistoryState("$scope:seen:${source.messageId}", "1")
                            if (source.imageCount > 0) imageAttachmentManager.restoreHistoryImages(source.requestId ?: source.messageId,
                                source.messageId, source.imageCount, download = false)
                        }
                        for (identity in operation.getAsJsonArray("requestIds") ?: JsonArray()) {
                            val requestId = identity.asString
                            chatRepository.markMessageError(requestId, "RESEND_NEW_REQUEST")
                            chatRepository.deleteMessage("pending_$requestId")
                        }
                    }
                    checkAccount()
                    if (next != null) {
                        chatRepository.putHistoryState(scope, next)
                        chatRepository.putHistoryState("$scope:generation", data.stringOrNull("datasetGeneration")!!)
                    }
                }
                normalized.maxOfOrNull { it.sourceTimestamp }?.let { historyCursorMs = maxOf(historyCursorMs, (it - 1).coerceAtLeast(0L)) }
                checkAccount()
                imageAttachmentManager.retryPendingHistoryImages(
                    accepts = { chatRepository.historyState("$scope:seen:$it") != null },
                    stillCurrent = { authRepository.getUserId() == userId })
                if (limited) return@withLock HistorySyncResult(false, inserted, updated, "当前服务端仅支持有限历史同步")
                cursor = next!!
                if (data.get("blocked")?.asBoolean == true) {
                    delay((data.get("pollAfterMs")?.asLong ?: 2000L).coerceIn(1000L, 30000L))
                    continue
                }
                if (data.get("hasMore")?.asBoolean != true) return@withLock HistorySyncResult(true, inserted, updated)
            }
            @Suppress("UNREACHABLE_CODE")
            HistorySyncResult(false)
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            Log.w("ChatTimelineSync", "历史同步失败", error)
            HistorySyncResult(false)
        }
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
