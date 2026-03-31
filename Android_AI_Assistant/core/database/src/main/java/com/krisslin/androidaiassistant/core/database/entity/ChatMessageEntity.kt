package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "chat_messages")
data class ChatMessageEntity(
    @PrimaryKey @ColumnInfo(name = "message_id") val messageId: String,
    @ColumnInfo(name = "session_id") val sessionId: String,
    val role: String,
    @ColumnInfo(name = "message_type") val messageType: String,
    val content: String,
    @ColumnInfo(name = "image_url") val imageUrl: String? = null,
    @ColumnInfo(name = "weather_attached") val weatherAttached: Int = 0,
    val status: String,
    val timestamp: Long,
    @ColumnInfo(name = "error_code") val errorCode: String? = null
)