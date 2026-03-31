package com.krisslin.androidaiassistant.core.database

import androidx.room.Database
import androidx.room.RoomDatabase
import com.krisslin.androidaiassistant.core.database.dao.BotNotificationDao
import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity

@Database(
    entities = [ChatMessageEntity::class, BotNotificationEntity::class],
    version = 1,
    exportSchema = false
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun chatMessageDao(): ChatMessageDao
    abstract fun botNotificationDao(): BotNotificationDao
}