package com.krisslin.androidaiassistant.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.Gson
import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.ChatMessagePayload
import com.krisslin.androidaiassistant.core.network.ws.ChatMessageRequest
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser
import com.krisslin.androidaiassistant.core.network.ws.WebSocketEvent
import com.krisslin.androidaiassistant.core.push.ReminderScheduler
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject

data class ChatMessageUi(
    val id: String,
    val role: String,
    val content: String,
    val timestamp: Long = System.currentTimeMillis(),
    val isStreaming: Boolean = false
)

/**
 * 连接状态
 */
enum class ConnectionStatus {
    CONNECTING,     // 连接中
    CONNECTED,      // 已连接
    DISCONNECTED    // 已断开
}

/**
 * Bot 活动状态（用于顶部提示显示）
 */
enum class BotActivityStatus {
    IDLE,       // 空闲：不显示任何内容
    SENDING,    // 发送中：用户消息已发出，等待服务器确认
    TYPING      // 输入中：Bot 正在生成回复
}

data class ChatUiState(
    val connectionStatus: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    val botActivity: BotActivityStatus = BotActivityStatus.IDLE,
    val input: String = "",
    val error: String? = null,
    val messages: List<ChatMessageUi> = emptyList()
)

/**
 * Side Effect 事件
 */
sealed interface ChatSideEffect {
    data object NavigateToLogin : ChatSideEffect
    data class ShowToast(val message: String) : ChatSideEffect
}

@HiltViewModel
class ChatViewModel @Inject constructor(
    private val chatApi: ChatApi,
    private val chatRepository: ChatRepository,
    private val botNotificationRepository: BotNotificationRepository,
    private val authRepository: AuthRepository,
    private val botWebSocketClient: BotWebSocketClient,
    private val reminderScheduler: ReminderScheduler,
    private val gson: Gson
) : ViewModel() {

    private val _uiState = MutableStateFlow(ChatUiState())
    val uiState: StateFlow<ChatUiState> = _uiState.asStateFlow()

    private val _sideEffects = MutableSharedFlow<ChatSideEffect>()
    val sideEffects: SharedFlow<ChatSideEffect> = _sideEffects.asSharedFlow()

    private val messageParser = IncomingMessageParser(gson)

    // 当前会话 ID
    private val sessionId: String = "default_session"

    // 流式消息内容缓存: requestId -> 累积内容
    private val streamingContentCache = ConcurrentHashMap<String, StringBuilder>()

    // 流式消息超时任务: requestId -> Job
    private val streamingTimeoutJobs = ConcurrentHashMap<String, Job>()
    private val pendingRequestIds = ConcurrentHashMap.newKeySet<String>()

    // 流式消息处理锁，防止竞态条件
    private val streamingMutex = Mutex()

    // 追踪已收到首帧的 requestId（用于判断是否切换为 TYPING 状态）
    private val receivedFirstChunk = ConcurrentHashMap.newKeySet<String>()

    // 流式消息超时时间（毫秒）
    private val streamingTimeoutMs = 180_000L

    private var lastSyncedTimestampMs: Long = 0L

    init {
        loadHistoryFromDb()
        observeAuthState()
        observeWebSocketEvents()
    }

    /**
     * 从 Room 数据库加载历史消息
     */
    private fun loadHistoryFromDb() {
        viewModelScope.launch {
            chatRepository.observeMessages(sessionId).collect { entities ->
                val uiMessages = entities.map { it.toUiModel() }
                lastSyncedTimestampMs = entities.maxOfOrNull { it.timestamp } ?: lastSyncedTimestampMs
                _uiState.update { it.copy(messages = uiMessages) }
            }
        }
    }

    /**
     * 观察认证状态变化
     */
    private fun observeAuthState() {
        viewModelScope.launch {
            authRepository.tokenState.collect { state ->
                when (state) {
                    is TokenState.Unauthenticated -> {
                        _sideEffects.emit(ChatSideEffect.NavigateToLogin)
                    }
                    is TokenState.Authenticated -> {
                        // WebSocket 连接由 WebSocketService 管理
                    }
                }
            }
        }
    }

    /**
     * 观察 WebSocket 事件
     */
    private fun observeWebSocketEvents() {
        viewModelScope.launch {
            botWebSocketClient.events.collect { event ->
                when (event) {
                    is WebSocketEvent.Connected -> {
                        _uiState.update { it.copy(connectionStatus = ConnectionStatus.CONNECTED, error = null) }
                        syncHistoryFromServer()
                    }
                    is WebSocketEvent.Disconnected -> {
                        _uiState.update { it.copy(connectionStatus = ConnectionStatus.DISCONNECTED) }
                    }
                    is WebSocketEvent.Message -> {
                        handleIncomingMessage(event.text)
                    }
                    is WebSocketEvent.Failure -> {
                        _uiState.update { 
                            it.copy(
                                connectionStatus = ConnectionStatus.DISCONNECTED,
                                error = "连接失败: ${event.throwable.message}"
                            ) 
                        }
                    }
                }
            }
        }
    }

    /**
     * 处理接收到的 WebSocket 消息
     */
    private fun handleIncomingMessage(raw: String) {
        viewModelScope.launch {
            when (val message = messageParser.parse(raw)) {
                is IncomingMessage.Reply -> {
                    handleReply(message)
                }
                is IncomingMessage.ReplyStream -> {
                    handleReplyStream(message)
                }
                is IncomingMessage.BotError -> {
                    handleBotError(message)
                }
                is IncomingMessage.Typing -> {
                    // 可选：显示"对方正在输入..."
                }
                is IncomingMessage.AuthExpired -> {
                    handleAuthExpired()
                }
                is IncomingMessage.Unknown -> {
                    // 忽略未知消息
                }
            }
        }
    }

    /**
     * 处理完整回复（非流式）
     */
    private suspend fun handleReply(message: IncomingMessage.Reply) {
        val requestId = message.requestId ?: return
        val payload = message.payload

        // 收到回复，切换为 TYPING 状态
        _uiState.update { it.copy(botActivity = BotActivityStatus.TYPING) }

        // 取消超时任务
        streamingTimeoutJobs.remove(requestId)?.cancel()
        streamingContentCache.remove(requestId)

        // 标记用户消息已发送
        chatRepository.markUserMessageSent(requestId)

        val segments = splitBotSegments(payload.content)
        if (segments.isEmpty()) {
            chatRepository.saveBotMessage(
                messageId = payload.messageId,
                sessionId = sessionId,
                content = payload.content,
                isStreaming = false
            )
        } else {
            segments.forEachIndexed { index, segment ->
                chatRepository.saveBotMessage(
                    messageId = "${payload.messageId}_$index",
                    sessionId = sessionId,
                    content = segment,
                    isStreaming = false
                )
            }
        }

        // 回复完成，移除 pendingId 并回到 IDLE
        pendingRequestIds.remove(requestId)
        receivedFirstChunk.remove(requestId)
        _uiState.update {
            it.copy(botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING)
        }
    }

    /**
     * 处理流式回复
     */
    private suspend fun handleReplyStream(message: IncomingMessage.ReplyStream) {
        val requestId = message.requestId ?: return
        val payload = message.payload
        val pendingMessageId = "pending_$requestId"

        streamingMutex.withLock {
            runCatching {
                if (payload.done) {
                    // 流式完成
                    streamingTimeoutJobs.remove(requestId)?.cancel()
                    val cachedContent = streamingContentCache.remove(requestId)?.toString()

                    // 标记被防抖合并的所有用户消息已发送
                    val relatedIds = payload.requestIds?.takeIf { it.isNotEmpty() } ?: listOf(requestId)
                    relatedIds.forEach { id ->
                        runCatching { chatRepository.markUserMessageSent(id) }
                        pendingRequestIds.remove(id)
                        receivedFirstChunk.remove(id)
                        streamingTimeoutJobs.remove(id)?.cancel()
                    }

                    val finalContent = payload.finalContent ?: cachedContent ?: ""
                    val finalMessageId = payload.messageId ?: "bot_$requestId"
                    val segments = splitBotSegments(finalContent)

                    // 删除流式占位消息（如果存在）
                    if (cachedContent != null) {
                        chatRepository.deleteMessage(pendingMessageId)
                    }

                    // 保存分条气泡
                    if (segments.isEmpty()) {
                        chatRepository.saveBotMessage(
                            messageId = finalMessageId,
                            sessionId = sessionId,
                            content = finalContent,
                            isStreaming = false
                        )
                    } else {
                        segments.forEachIndexed { index, segment ->
                            chatRepository.saveBotMessage(
                                messageId = "${finalMessageId}_$index",
                                sessionId = sessionId,
                                content = segment,
                                isStreaming = false
                            )
                        }
                    }

                    payload.timerInstruction?.let { timer ->
                        val scheduleKey = relatedIds.joinToString("_")
                        reminderScheduler.schedule(
                            requestId = scheduleKey.ifBlank { requestId },
                            target = timer.target,
                            text = timer.text
                        )
                    }

                    // 流式完成，回到 IDLE 或保持 SENDING（如有其他待处理消息）
                    _uiState.update {
                        it.copy(botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING)
                    }
                } else {
                    // 流式增量
                    val delta = payload.delta
                    if (delta.isNotEmpty()) {
                        // 首帧到达时切换为 TYPING 状态
                        if (receivedFirstChunk.add(requestId)) {
                            _uiState.update { it.copy(botActivity = BotActivityStatus.TYPING) }
                        }

                        // 确保占位消息存在后再追加
                        if (!streamingContentCache.containsKey(requestId)) {
                            chatRepository.saveBotMessage(
                                messageId = pendingMessageId,
                                sessionId = sessionId,
                                content = delta,
                                isStreaming = true
                            )
                            streamingContentCache[requestId] = StringBuilder(delta)
                        } else {
                            streamingContentCache[requestId]!!.append(delta)
                            chatRepository.appendStreamingContent(pendingMessageId, delta)
                        }
                        resetStreamingTimeout(requestId, pendingMessageId)
                    }
                }
            }.onFailure { e ->
                android.util.Log.e("ChatViewModel", "Stream handling error: ${e.message}", e)
            }
        }
    }

    /**
     * 处理 Bot 错误
     */
    private suspend fun handleBotError(message: IncomingMessage.BotError) {
        val payload = message.payload
        val relatedIds = buildList {
            message.requestId?.let { add(it) }
            addAll(payload.requestIds)
        }.distinct()
        relatedIds.forEach { requestId ->
            streamingTimeoutJobs.remove(requestId)?.cancel()
            streamingContentCache.remove(requestId)
            pendingRequestIds.remove(requestId)
            receivedFirstChunk.remove(requestId)
            chatRepository.markMessageError("pending_$requestId", payload.errorCode)
            chatRepository.markMessageError(requestId, payload.errorCode)
        }
        _uiState.update { 
            it.copy(
                botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING,
                error = "${payload.errorCode}: ${payload.message}"
            ) 
        }

        botNotificationRepository.addError(
            errorCode = payload.errorCode,
            message = payload.message,
            timestamp = payload.timestamp
        )
    }

    /**
     * 处理认证过期
     */
    private suspend fun handleAuthExpired() {
        botWebSocketClient.disconnect()

        // 尝试刷新 Token
        val newToken = authRepository.refreshToken()
        if (newToken != null) {
            botWebSocketClient.connect(newToken)
        } else {
            _sideEffects.emit(ChatSideEffect.NavigateToLogin)
        }
    }

    /**
     * 重置流式消息超时计时器
     */
    private fun resetStreamingTimeout(requestId: String, pendingMessageId: String) {
        streamingTimeoutJobs[requestId]?.cancel()
        streamingTimeoutJobs[requestId] = viewModelScope.launch {
            delay(streamingTimeoutMs)
            // 超时仍在 streaming → 标记错误
            streamingContentCache.remove(requestId)
            pendingRequestIds.remove(requestId)
            receivedFirstChunk.remove(requestId)
            chatRepository.markMessageError(pendingMessageId, "TIMEOUT")
            _uiState.update { 
                it.copy(
                    botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING, 
                    error = "AI 响应超时，请重试"
                ) 
            }
        }
    }

    fun onInputChange(value: String) {
        _uiState.update { it.copy(input = value) }
    }

    fun clearError() {
        _uiState.update { it.copy(error = null) }
    }

    /**
     * 发送文本消息
     */
    fun sendText() {
        val current = _uiState.value.input.trim()
        if (current.isEmpty()) return

        // 检查连接状态
        if (_uiState.value.connectionStatus != ConnectionStatus.CONNECTED) {
            _uiState.update { it.copy(error = "未连接到服务器") }
            return
        }

        val requestId = UUID.randomUUID().toString()
        pendingRequestIds.add(requestId)
        _uiState.update { it.copy(input = "", botActivity = BotActivityStatus.SENDING, error = null) }

        viewModelScope.launch {
            // 保存用户消息到 DB
            chatRepository.saveUserMessage(
                messageId = requestId,
                sessionId = sessionId,
                content = current
            )

            // 构建 WebSocket 消息
            val wsMessage = ChatMessageRequest(
                requestId = requestId,
                payload = ChatMessagePayload(
                    messageType = "text",
                    content = current,
                    timestamp = System.currentTimeMillis()
                )
            )

            // 发送
            val sent = botWebSocketClient.send(gson.toJson(wsMessage))
            if (!sent) {
                pendingRequestIds.remove(requestId)
                chatRepository.markMessageError(requestId, "SEND_FAILED")
                chatRepository.markMessageError("pending_$requestId", "SEND_FAILED")
                _uiState.update { 
                    it.copy(
                        botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING, 
                        error = "消息发送失败"
                    ) 
                }
                return@launch
            }

            // 启动超时计时器
            resetStreamingTimeout(requestId, "pending_$requestId")
        }
    }

    /**
     * 手动重连
     */
    fun reconnect() {
        if (_uiState.value.connectionStatus != ConnectionStatus.CONNECTING) {
            val token = authRepository.getAccessToken() ?: ""
            _uiState.update { it.copy(connectionStatus = ConnectionStatus.CONNECTING) }
            botWebSocketClient.connect(token)
        }
    }

    override fun onCleared() {
        super.onCleared()
        streamingTimeoutJobs.values.forEach { it.cancel() }
        streamingTimeoutJobs.clear()
        streamingContentCache.clear()
        pendingRequestIds.clear()
        receivedFirstChunk.clear()
    }

    private fun splitBotSegments(content: String): List<String> {
        return content
            .split('\n')
            .map { it.trim() }
            .filter { it.isNotBlank() }
    }

    private fun syncHistoryFromServer() {
        viewModelScope.launch {
            runCatching {
                chatApi.history(
                    since = lastSyncedTimestampMs,
                    limit = 300
                )
            }.onSuccess { response ->
                val items = response.getAsJsonObject("data")
                    ?.getAsJsonArray("items")
                    ?: JsonArray()
                applyHistoryItems(items)
            }.onFailure {
                // 历史补拉失败不影响实时聊天
            }
        }
    }

    private suspend fun applyHistoryItems(items: JsonArray) {
        for (i in 0 until items.size()) {
            runCatching {
                val item = items[i].asJsonObjectOrNull() ?: return@runCatching
                val userId = item.getStringOrNull("userId")
                if (userId != null && userId != "default-user") return@runCatching
                val messageId = item.getStringOrNull("messageId") ?: return@runCatching
                val role = item.getStringOrNull("role") ?: return@runCatching
                val content = item.getStringOrNull("content") ?: return@runCatching
                val timestamp = item.getLongOrNull("timestamp") ?: return@runCatching

                if (role == "user") {
                    chatRepository.saveExternalMessage(
                        messageId = messageId,
                        sessionId = sessionId,
                        role = MessageRole.USER,
                        content = content,
                        timestamp = timestamp
                    )
                } else {
                    val segments = splitBotSegments(content)
                    if (segments.isEmpty()) {
                        chatRepository.saveExternalMessage(
                            messageId = messageId,
                            sessionId = sessionId,
                            role = MessageRole.BOT,
                            content = content,
                            timestamp = timestamp
                        )
                    } else {
                        segments.forEachIndexed { index, segment ->
                            chatRepository.saveExternalMessage(
                                messageId = "${messageId}_$index",
                                sessionId = sessionId,
                                role = MessageRole.BOT,
                                content = segment,
                                timestamp = timestamp + index
                            )
                        }
                    }
                }

                if (timestamp > lastSyncedTimestampMs) {
                    lastSyncedTimestampMs = timestamp
                }
            }
        }
    }

    private fun ChatMessageEntity.toUiModel() = ChatMessageUi(
        id = messageId,
        role = role,
        content = content,
        timestamp = timestamp,
        isStreaming = status == MessageStatus.STREAMING
    )

    private fun JsonObject.getStringOrNull(name: String): String? {
        val v = get(name) ?: return null
        return if (v.isJsonNull) null else v.asString
    }

    private fun JsonObject.getLongOrNull(name: String): Long? {
        val v = get(name) ?: return null
        return if (v.isJsonNull) null else v.asLong
    }

    private fun com.google.gson.JsonElement.asJsonObjectOrNull(): JsonObject? {
        return if (isJsonObject) asJsonObject else null
    }
}
