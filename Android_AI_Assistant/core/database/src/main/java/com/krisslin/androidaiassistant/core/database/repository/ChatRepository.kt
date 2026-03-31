package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 聊天消息状态
 */
object MessageStatus {
    const val SENDING = "sending"
    const val SENT = "sent"
    const val RECEIVED = "received"
    const val STREAMING = "streaming"
    const val ERROR = "error"
}

/**
 * 消息类型
 */
object MessageType {
    const val TEXT = "text"
    const val IMAGE = "image"
    const val WEATHER = "weather"
}

/**
 * 消息角色
 */
object MessageRole {
    const val USER = "user"
    const val BOT = "bot"
    const val SYSTEM = "system"
}

/**
 * ChatRepository 封装聊天消息的数据访问
 */
@Singleton
class ChatRepository @Inject constructor(
    private val chatMessageDao: ChatMessageDao
) {
    /**
     * 观察指定会话的消息（按时间升序）
     */
    fun observeMessages(sessionId: String, limit: Int = 100): Flow<List<ChatMessageEntity>> {
        return chatMessageDao.observeLatest(sessionId, limit)
    }

    /**
     * 一次性获取指定会话的消息
     */
    suspend fun getMessages(sessionId: String, limit: Int = 100): List<ChatMessageEntity> {
        return chatMessageDao.getLatest(sessionId, limit)
    }

    /**
     * 保存用户消息（发送中状态）
     */
    suspend fun saveUserMessage(
        messageId: String,
        sessionId: String,
        content: String
    ): ChatMessageEntity {
        val entity = ChatMessageEntity(
            messageId = messageId,
            sessionId = sessionId,
            role = MessageRole.USER,
            messageType = MessageType.TEXT,
            content = content,
            status = MessageStatus.SENDING,
            timestamp = System.currentTimeMillis()
        )
        chatMessageDao.upsert(entity)
        return entity
    }

    /**
     * 标记用户消息已发送成功
     */
    suspend fun markUserMessageSent(messageId: String) {
        val existing = chatMessageDao.getById(messageId) ?: return
        chatMessageDao.upsert(existing.copy(status = MessageStatus.SENT))
    }

    /**
     * 保存 Bot 回复消息
     */
    suspend fun saveBotMessage(
        messageId: String,
        sessionId: String,
        content: String,
        isStreaming: Boolean = false
    ): ChatMessageEntity {
        val entity = ChatMessageEntity(
            messageId = messageId,
            sessionId = sessionId,
            role = MessageRole.BOT,
            messageType = MessageType.TEXT,
            content = content,
            status = if (isStreaming) MessageStatus.STREAMING else MessageStatus.RECEIVED,
            timestamp = System.currentTimeMillis()
        )
        chatMessageDao.upsert(entity)
        return entity
    }

    /**
     * 更新流式消息内容
     */
    suspend fun updateStreamingContent(messageId: String, content: String, finished: Boolean) {
        val status = if (finished) MessageStatus.RECEIVED else MessageStatus.STREAMING
        chatMessageDao.updateContent(messageId, content, status)
    }

    /**
     * 标记消息为错误状态
     */
    suspend fun markMessageError(messageId: String, errorCode: String? = null) {
        val existing = chatMessageDao.getById(messageId) ?: return
        chatMessageDao.upsert(existing.copy(status = MessageStatus.ERROR, errorCode = errorCode))
    }

    /**
     * 清空会话消息
     */
    suspend fun clearSession(sessionId: String) {
        chatMessageDao.deleteBySession(sessionId)
    }
}
