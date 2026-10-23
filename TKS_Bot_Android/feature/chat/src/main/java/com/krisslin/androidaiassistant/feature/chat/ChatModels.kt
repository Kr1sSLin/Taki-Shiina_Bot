package com.krisslin.androidaiassistant.feature.chat

import com.krisslin.androidaiassistant.core.database.repository.ContentType
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import com.krisslin.androidaiassistant.core.database.repository.ModelProvider

internal fun chatInputTooLong(text: String): Boolean =
    text.codePointCount(0, text.length) > 8000 || text.toByteArray(Charsets.UTF_8).size > 32 * 1024

internal fun chatRecoveryCode(action: String?, code: String): String = when (action) {
    "resend_new_request" -> "RESEND_NEW_REQUEST"
    "edit_input" -> "EDIT_INPUT"
    "poll_status" -> "POLL_STATUS"
    "retry_same_request" -> "RETRY_SAME_REQUEST"
    else -> code
}

data class ChatAttachmentUi(
    val id: String,
    val localUri: String,
    val mimeType: String,
    val fileSize: Long,
    val width: Int? = null,
    val height: Int? = null
)

data class ChatMessageUi(
    val id: String,
    val role: String,
    val content: String,
    val contentType: String = ContentType.TEXT,
    val modelProvider: String = ModelProvider.DEEPSEEK,
    val attachments: List<ChatAttachmentUi> = emptyList(),
    val timestamp: Long = System.currentTimeMillis(),
    val status: String = MessageStatus.RECEIVED,
    val errorCode: String? = null,
    val isStreaming: Boolean = false
)

enum class ConnectionStatus {
    CONNECTING,
    CONNECTED,
    DISCONNECTED
}

enum class BotActivityStatus {
    IDLE,
    SENDING,
    TYPING
}

data class ChatUiState(
    val connectionStatus: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    val botActivity: BotActivityStatus = BotActivityStatus.IDLE,
    val botStage: String? = null,
    val input: String = "",
    val error: String? = null,
    val selectedImages: List<ChatAttachmentUi> = emptyList(),
    val messages: List<ChatMessageUi> = emptyList(),
    val balance: Int = 0,
    val levelName: String = "",
    val continuousDays: Int = 0,
    val levelCelebration: LevelCelebrationUi? = null
)

data class LevelCelebrationUi(
    val changeType: String,
    val levelName: String,
    val continuousDays: Int,
    val nextLevelName: String? = null,
    val daysToNextLevel: Int? = null
)

sealed interface ChatSideEffect {
    data object NavigateToLogin : ChatSideEffect
    data class ShowToast(val message: String) : ChatSideEffect
}
