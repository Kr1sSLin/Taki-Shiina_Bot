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

    @Query("SELECT * FROM chat_messages WHERE session_id = :sessionId ORDER BY timestamp ASC LIMIT :limit")
    fun observeLatest(sessionId: String, limit: Int = 100): Flow<List<ChatMessageEntity>>

    @Query("SELECT * FROM chat_messages WHERE session_id = :sessionId ORDER BY timestamp ASC LIMIT :limit")
    suspend fun getLatest(sessionId: String, limit: Int = 100): List<ChatMessageEntity>

    @Query("SELECT * FROM chat_messages WHERE message_id = :messageId")
    suspend fun getById(messageId: String): ChatMessageEntity?

    @Query("UPDATE chat_messages SET content = :content, status = :status WHERE message_id = :messageId")
    suspend fun updateContent(messageId: String, content: String, status: String)

    @Query("UPDATE chat_messages SET content = content || :delta WHERE message_id = :messageId")
    suspend fun appendContent(messageId: String, delta: String)

    @Query("UPDATE chat_messages SET message_id = :newId, status = :status WHERE message_id = :oldId")
    suspend fun finalizeMessage(oldId: String, newId: String, status: String)

    @Query("DELETE FROM chat_messages WHERE session_id = :sessionId")
    suspend fun deleteBySession(sessionId: String)
}