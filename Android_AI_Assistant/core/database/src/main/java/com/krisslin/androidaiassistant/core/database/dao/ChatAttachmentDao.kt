package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Upsert
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface ChatAttachmentDao {
    // 同 ChatMessageDao：REPLACE 会删行重建，配合消息表 REPLACE 时互相级联丢失，
    // 统一改用 @Upsert 原地更新。
    @Upsert
    suspend fun upsertAll(items: List<ChatAttachmentEntity>)

    @Query("SELECT * FROM chat_attachments WHERE session_id = :sessionId ORDER BY timestamp ASC")
    fun observeBySession(sessionId: String): Flow<List<ChatAttachmentEntity>>

    @Query("SELECT * FROM chat_attachments WHERE message_id IN (:messageIds)")
    suspend fun getByMessageIds(messageIds: List<String>): List<ChatAttachmentEntity>

    @Query("SELECT * FROM chat_attachments WHERE message_id = :messageId")
    suspend fun getByMessageId(messageId: String): List<ChatAttachmentEntity>

    @Query("DELETE FROM chat_attachments WHERE message_id = :messageId")
    suspend fun deleteByMessageId(messageId: String)

    @Query("DELETE FROM chat_attachments WHERE session_id = :sessionId")
    suspend fun deleteBySession(sessionId: String)
}
