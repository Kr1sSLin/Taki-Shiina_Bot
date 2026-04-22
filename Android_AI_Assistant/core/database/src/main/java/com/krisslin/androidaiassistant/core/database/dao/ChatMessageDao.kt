package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Update
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface ChatMessageDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(message: ChatMessageEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
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
