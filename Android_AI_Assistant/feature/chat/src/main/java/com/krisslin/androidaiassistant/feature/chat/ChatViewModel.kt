package com.krisslin.androidaiassistant.feature.chat

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.Gson
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.ChatHttpRequest
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID
import javax.inject.Inject

data class ChatMessageUi(
    val id: String,
    val role: String,
    val content: String,
    val isStreaming: Boolean = false
)

/**
 * 连接状态（HTTP 模式下简化为在线/离线）
 */
enum class ConnectionStatus {
    CONNECTED,      // 在线
    DISCONNECTED    // 离线（请求失败时）
}

data class ChatUiState(
    val connectionStatus: ConnectionStatus = ConnectionStatus.CONNECTED,
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
    private val chatApi: ChatApi,
    private val chatRepository: ChatRepository,
    private val authRepository: AuthRepository,
    private val gson: Gson
) : ViewModel() {

    private val _uiState = MutableStateFlow(ChatUiState())
    val uiState: StateFlow<ChatUiState> = _uiState.asStateFlow()

    private val _sideEffects = MutableSharedFlow<ChatSideEffect>()
    val sideEffects: SharedFlow<ChatSideEffect> = _sideEffects.asSharedFlow()

    // 当前会话 ID（实际使用时应从 SessionManager 获取）
    private val sessionId: String = "default_session"

    init {
        loadHistoryFromDb()
        observeAuthState()
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
                        _sideEffects.emit(ChatSideEffect.NavigateToLogin)
                    }
                    is TokenState.Authenticated -> {
                        // HTTP 模式下无需额外操作
                    }
                }
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

        val requestId = UUID.randomUUID().toString()
        _uiState.update { it.copy(input = "", sending = true, error = null) }

        viewModelScope.launch {
            // 先保存用户消息到 DB
            chatRepository.saveUserMessage(
                messageId = requestId,
                sessionId = sessionId,
                content = current
            )

            runCatching {
                chatApi.chat(
                    request = ChatHttpRequest(
                        requestId = requestId,
                        message = current,
                        stream = false
                    )
                )
            }.onSuccess { response ->
                // 标记用户消息发送成功
                chatRepository.markUserMessageSent(requestId)

                // 解析并保存 Bot 回复
                val content = response.getAsJsonObject("data")?.get("reply")?.asString
                    ?: response.getAsJsonObject("data")?.get("message")?.asString
                    ?: "解析回复失败"

                val botMessageId = response.getAsJsonObject("data")
                    ?.get("messageId")?.asString
                    ?: UUID.randomUUID().toString()

                chatRepository.saveBotMessage(
                    messageId = botMessageId,
                    sessionId = sessionId,
                    content = content,
                    isStreaming = false
                )

                _uiState.update { 
                    it.copy(
                        sending = false, 
                        connectionStatus = ConnectionStatus.CONNECTED
                    ) 
                }
            }.onFailure { throwable ->
                chatRepository.markMessageError(requestId)
                _uiState.update {
                    it.copy(
                        sending = false,
                        connectionStatus = ConnectionStatus.DISCONNECTED,
                        error = throwable.message ?: "请求失败"
                    )
                }
            }
        }
    }

    private fun ChatMessageEntity.toUiModel() = ChatMessageUi(
        id = messageId,
        role = role,
        content = content,
        isStreaming = status == "streaming"
    )
}
