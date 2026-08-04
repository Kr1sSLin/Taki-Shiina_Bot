package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Upsert
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface ChatMessageDao {
    // 注意：必须用 @Upsert（INSERT ... ON CONFLICT DO UPDATE）。
    // 若用 @Insert(REPLACE)，SQLite 会先 DELETE 旧行再 INSERT，
    // 触发 chat_attachments 的外键 CASCADE，把消息附件（图片）一并删掉。
    @Upsert
    suspend fun upsert(message: ChatMessageEntity)

    @Upsert
    suspend fun upsertAll(messages: List<ChatMessageEntity>)

    @Query("SELECT * FROM (SELECT * FROM chat_messages WHERE session_id = :sessionId ORDER BY timestamp DESC LIMIT :limit) ORDER BY timestamp ASC")
    fun observeLatest(sessionId: String, limit: Int = 300): Flow<List<ChatMessageEntity>>

    @Query("SELECT * FROM (SELECT * FROM chat_messages WHERE session_id = :sessionId ORDER BY timestamp DESC LIMIT :limit) ORDER BY timestamp ASC")
    suspend fun getLatest(sessionId: String, limit: Int = 300): List<ChatMessageEntity>

    @Query("SELECT * FROM chat_messages WHERE message_id = :messageId")
    suspend fun getById(messageId: String): ChatMessageEntity?

    @Query("UPDATE chat_messages SET content = :content, status = :status WHERE message_id = :messageId")
    suspend fun updateContent(messageId: String, content: String, status: String)

    @Query("UPDATE chat_messages SET content = content || :delta WHERE message_id = :messageId")
    suspend fun appendContent(messageId: String, delta: String)

    @Query("UPDATE chat_messages SET message_id = :newId, content = :content, status = :status WHERE message_id = :oldId")
    suspend fun finalizeMessage(oldId: String, newId: String, content: String, status: String)

    @Query("DELETE FROM chat_messages WHERE session_id = :sessionId")
    suspend fun deleteBySession(sessionId: String)

    @Query("UPDATE chat_messages SET status = 'received' WHERE session_id = :sessionId AND status = 'streaming'")
    suspend fun normalizeStreamingMessages(sessionId: String)

    @Query("DELETE FROM chat_messages WHERE message_id = :messageId")
    suspend fun deleteById(messageId: String)
}
