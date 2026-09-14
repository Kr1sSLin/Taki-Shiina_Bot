package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "bot_notifications")
data class BotNotificationEntity(
    @PrimaryKey @ColumnInfo(name = "notification_id") val notificationId: String,
    @ColumnInfo(name = "error_code") val errorCode: String,
    val message: String,
    @ColumnInfo(name = "is_read") val isRead: Int = 0,
    val timestamp: Long
)