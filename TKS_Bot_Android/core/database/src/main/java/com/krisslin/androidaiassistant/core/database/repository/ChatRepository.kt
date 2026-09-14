package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import com.krisslin.androidaiassistant.core.database.dao.ChatAttachmentDao
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
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

object ContentType {
    const val TEXT = "text"
    const val IMAGE = "image"
    const val MIXED = "mixed"
}

object ModelProvider {
    const val DEEPSEEK = "deepseek"
    const val GEMINI = "gemini"
}

/**
 * 历史同步写入单条消息的结果
 */
enum class SyncOutcome { INSERTED, UPDATED, UNCHANGED }

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
    private val chatMessageDao: ChatMessageDao,
    private val chatAttachmentDao: ChatAttachmentDao
) {
    /**
     * 观察指定会话的消息（按时间升序）
     */
    fun observeMessages(sessionId: String, limit: Int = 300): Flow<List<ChatMessageEntity>> {
        return chatMessageDao.observeLatest(sessionId, limit)
    }

    /**
     * 观察消息及其附件：消息表与附件表任一变化都会重新发射，
     * 保证发送后/历史重载时附件（图片）立即可见。
     */
    fun observeMessagesWithAttachments(
        sessionId: String,
        limit: Int = 300
    ): Flow<List<Pair<ChatMessageEntity, List<ChatAttachmentEntity>>>> {
        return combine(
            chatMessageDao.observeLatest(sessionId, limit),
            chatAttachmentDao.observeBySession(sessionId)
        ) { messages, attachments ->
            val byMessage = attachments.groupBy { it.messageId }
            messages.map { message ->
                message to (byMessage[message.messageId] ?: emptyList())
            }
        }
    }

    /**
     * 一次性获取指定会话的消息
     */
    suspend fun getMessages(sessionId: String, limit: Int = 300): List<ChatMessageEntity> {
        return chatMessageDao.getLatest(sessionId, limit)
    }

    /**
     * 保存用户消息（发送中状态）
     */
    suspend fun saveUserMessage(
        messageId: String,
        sessionId: String,
        content: String,
        contentType: String = ContentType.TEXT,
        modelProvider: String = ModelProvider.DEEPSEEK
    ): ChatMessageEntity {
        val entity = ChatMessageEntity(
            messageId = messageId,
            sessionId = sessionId,
            role = MessageRole.USER,
            messageType = MessageType.TEXT,
            contentType = contentType,
            modelProvider = modelProvider,
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
        isStreaming: Boolean = false,
        contentType: String = ContentType.TEXT,
        modelProvider: String = ModelProvider.DEEPSEEK,
        timestamp: Long = System.currentTimeMillis()
    ): ChatMessageEntity {
        val entity = ChatMessageEntity(
            messageId = messageId,
            sessionId = sessionId,
            role = MessageRole.BOT,
            messageType = MessageType.TEXT,
            contentType = contentType,
            modelProvider = modelProvider,
            content = content,
            status = if (isStreaming) MessageStatus.STREAMING else MessageStatus.RECEIVED,
            timestamp = timestamp
        )
        chatMessageDao.upsert(entity)
        return entity
    }

    /**
     * 保存外部同步消息（历史补拉）
     *
     * 服务端已经返回这条消息，说明它确实落库了。所以本地那条卡在
     * sending / error 的行必须被修回来——本地的「发送失败」多数是
     * 响应没收到（超时、代理、断线），不代表服务端没收到。
     *
     * 旧实现是 `if (existing != null) return`，已存在就直接返回，
     * 于是 error 状态永远不会被和解，UI 上的「发送失败」永久留着。
     *
     * streaming 的行不动：那是正在进行的流式输出，覆盖会打断它。
     */
    suspend fun saveExternalMessage(
        messageId: String,
        sessionId: String,
        role: String,
        content: String,
        timestamp: Long,
        contentType: String = ContentType.TEXT,
        modelProvider: String = ModelProvider.DEEPSEEK
    ): SyncOutcome {
        // 服务端确认过的最终状态：用户消息=已发送，bot 消息=已接收
        val syncedStatus =
            if (role == MessageRole.USER) MessageStatus.SENT else MessageStatus.RECEIVED

        val existing = chatMessageDao.getById(messageId)
        if (existing == null) {
            chatMessageDao.upsert(
                ChatMessageEntity(
                    messageId = messageId,
                    sessionId = sessionId,
                    role = role,
                    messageType = MessageType.TEXT,
                    contentType = contentType,
                    modelProvider = modelProvider,
                    content = content,
                    status = syncedStatus,
                    timestamp = timestamp
                )
            )
            return SyncOutcome.INSERTED
        }

        if (existing.status == MessageStatus.STREAMING) return SyncOutcome.UNCHANGED

        // 本地内容为空才用服务端内容补齐；否则保留本地（bot 气泡是拆分后的片段）
        val healedContent = if (existing.content.isBlank()) content else existing.content
        val healed = existing.copy(
            status = syncedStatus,
            errorCode = null,
            content = healedContent
        )
        if (healed == existing) return SyncOutcome.UNCHANGED

        chatMessageDao.upsert(healed)
        return SyncOutcome.UPDATED
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
        chatAttachmentDao.deleteBySession(sessionId)
    }

    /**
     * 将会话中残留的流式消息收敛为已完成状态
     */
    suspend fun normalizeStreamingMessages(sessionId: String) {
        chatMessageDao.normalizeStreamingMessages(sessionId)
    }

    /**
     * 删除单条消息
     */
    suspend fun deleteMessage(messageId: String) {
        chatAttachmentDao.deleteByMessageId(messageId)
        chatMessageDao.deleteById(messageId)
    }

    /**
     * 清理历史遗留的空 bot 消息（后端旧版空回复落库的脏数据）
     */
    suspend fun deleteBlankBotMessages(): Int {
        return chatMessageDao.deleteBlankBotMessages()
    }

    /**
     * 追加流式消息内容（增量）
     */
    suspend fun appendStreamingContent(messageId: String, delta: String) {
        chatMessageDao.appendContent(messageId, delta)
    }

    /**
     * 完成流式消息，替换临时 ID 为最终 ID
     */
    suspend fun finalizeStreamingMessage(pendingId: String, finalId: String, finalContent: String) {
        chatMessageDao.finalizeMessage(pendingId, finalId, finalContent, MessageStatus.RECEIVED)
    }

    suspend fun saveMessageAttachments(items: List<ChatAttachmentEntity>) {
        if (items.isEmpty()) return
        chatAttachmentDao.upsertAll(items)
    }

    suspend fun getAttachmentsByMessageIds(messageIds: List<String>): List<ChatAttachmentEntity> {
        if (messageIds.isEmpty()) return emptyList()
        return chatAttachmentDao.getByMessageIds(messageIds)
    }

    suspend fun getAttachmentsByMessageId(messageId: String): List<ChatAttachmentEntity> {
        return chatAttachmentDao.getByMessageId(messageId)
    }
}
