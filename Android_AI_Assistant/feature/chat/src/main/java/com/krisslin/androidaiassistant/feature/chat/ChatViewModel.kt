package com.krisslin.androidaiassistant.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.Gson
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.ChatMessagePayload
import com.krisslin.androidaiassistant.core.network.ws.ChatMessageRequest
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser
import com.krisslin.androidaiassistant.core.network.ws.WebSocketEvent
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
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

data class ChatUiState(
    val connectionStatus: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    val sending: Boolean = false,
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
    private val chatRepository: ChatRepository,
    private val authRepository: AuthRepository,
    private val botWebSocketClient: BotWebSocketClient,
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

    // 流式消息超时时间（毫秒）
    private val streamingTimeoutMs = 60_000L

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
                        botWebSocketClient.disconnect()
                        _sideEffects.emit(ChatSideEffect.NavigateToLogin)
                    }
                    is TokenState.Authenticated -> {
                        connectWebSocket()
                    }
                }
            }
        }
    }

    /**
     * 连接 WebSocket
     */
    private fun connectWebSocket() {
        val token = authRepository.getAccessToken() ?: return
        _uiState.update { it.copy(connectionStatus = ConnectionStatus.CONNECTING) }
        botWebSocketClient.connect(token)
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

        // 取消超时任务
        streamingTimeoutJobs.remove(requestId)?.cancel()
        streamingContentCache.remove(requestId)

        // 标记用户消息已发送
        chatRepository.markUserMessageSent(requestId)

        // 保存 Bot 回复
        chatRepository.saveBotMessage(
            messageId = payload.messageId,
            sessionId = sessionId,
            content = payload.content,
            isStreaming = false
        )

        _uiState.update { it.copy(sending = false) }
    }

    /**
     * 处理流式回复
     */
    private suspend fun handleReplyStream(message: IncomingMessage.ReplyStream) {
        val requestId = message.requestId ?: return
        val payload = message.payload
        val pendingMessageId = "pending_$requestId"

        if (payload.done) {
            // 流式完成
            streamingTimeoutJobs.remove(requestId)?.cancel()
            streamingContentCache.remove(requestId)

            // 标记用户消息已发送
            chatRepository.markUserMessageSent(requestId)

            // 使用 finalContent（如果有）或累积的内容
            val finalContent = try {
                val payloadMap = gson.fromJson(gson.toJson(payload), Map::class.java)
                payloadMap["finalContent"]?.toString()
            } catch (e: Exception) {
                null
            }

            val messageId = try {
                val payloadMap = gson.fromJson(gson.toJson(payload), Map::class.java)
                payloadMap["messageId"]?.toString() ?: UUID.randomUUID().toString()
            } catch (e: Exception) {
                UUID.randomUUID().toString()
            }

            if (finalContent != null) {
                chatRepository.finalizeStreamingMessage(pendingMessageId, messageId, finalContent)
            } else {
                chatRepository.updateStreamingContent(pendingMessageId, "", finished = true)
            }

            _uiState.update { it.copy(sending = false) }
        } else {
            // 流式增量
            val delta = payload.delta
            if (delta.isNotEmpty()) {
                // 追加到缓存
                streamingContentCache.getOrPut(requestId) { StringBuilder() }.append(delta)

                // 追加到数据库
                chatRepository.appendStreamingContent(pendingMessageId, delta)

                // 重置超时计时器
                resetStreamingTimeout(requestId, pendingMessageId)
            }
        }
    }

    /**
     * 处理 Bot 错误
     */
    private suspend fun handleBotError(message: IncomingMessage.BotError) {
        val payload = message.payload
        _uiState.update { 
            it.copy(
                sending = false,
                error = "${payload.errorCode}: ${payload.message}"
            ) 
        }
    }

    /**
     * 处理认证过期
     */
    private suspend fun handleAuthExpired() {
        botWebSocketClient.disconnect()
        
        // 尝试刷新 Token
        val newToken = authRepository.refreshToken()
        if (newToken != null) {
            connectWebSocket()
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
            chatRepository.markMessageError(pendingMessageId, "TIMEOUT")
            _uiState.update { it.copy(sending = false, error = "AI 响应超时，请重试") }
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
        _uiState.update { it.copy(input = "", sending = true, error = null) }

        viewModelScope.launch {
            // 保存用户消息到 DB
            chatRepository.saveUserMessage(
                messageId = requestId,
                sessionId = sessionId,
                content = current
            )

            // 预创建 Bot 消息占位（streaming 状态）
            chatRepository.saveBotMessage(
                messageId = "pending_$requestId",
                sessionId = sessionId,
                content = "",
                isStreaming = true
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
                chatRepository.markMessageError(requestId, "SEND_FAILED")
                chatRepository.markMessageError("pending_$requestId", "SEND_FAILED")
                _uiState.update { it.copy(sending = false, error = "消息发送失败") }
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
            connectWebSocket()
        }
    }

    override fun onCleared() {
        super.onCleared()
        streamingTimeoutJobs.values.forEach { it.cancel() }
        streamingTimeoutJobs.clear()
        streamingContentCache.clear()
    }

    private fun ChatMessageEntity.toUiModel() = ChatMessageUi(
        id = messageId,
        role = role,
        content = content,
        timestamp = timestamp,
        isStreaming = status == MessageStatus.STREAMING
    )
}
