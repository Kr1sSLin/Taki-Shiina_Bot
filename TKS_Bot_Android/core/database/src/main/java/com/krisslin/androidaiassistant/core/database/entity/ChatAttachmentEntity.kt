package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.PrimaryKey

@Entity(
    tableName = "chat_attachments",
    foreignKeys = [
        ForeignKey(
            entity = ChatMessageEntity::class,
            parentColumns = ["message_id"],
            childColumns = ["message_id"],
            onDelete = ForeignKey.CASCADE
        )
    ],
    indices = [
        Index(value = ["message_id"]),
        Index(value = ["session_id", "timestamp"])
    ]
)
data class ChatAttachmentEntity(
    @PrimaryKey @ColumnInfo(name = "attachment_id") val attachmentId: String,
    @ColumnInfo(name = "message_id") val messageId: String,
    @ColumnInfo(name = "session_id") val sessionId: String,
    @ColumnInfo(name = "mime_type") val mimeType: String,
    @ColumnInfo(name = "local_uri") val localUri: String,
    @ColumnInfo(name = "file_size") val fileSize: Long,
    @ColumnInfo(name = "width") val width: Int? = null,
    @ColumnInfo(name = "height") val height: Int? = null,
    @ColumnInfo(name = "upload_state") val uploadState: String = "local",
    @ColumnInfo(name = "created_at") val createdAt: Long = System.currentTimeMillis(),
    val timestamp: Long = System.currentTimeMillis()
)
