package com.krisslin.androidaiassistant.core.database

import androidx.room.Database
import androidx.room.RoomDatabase
import com.krisslin.androidaiassistant.core.database.dao.BotNotificationDao
import com.krisslin.androidaiassistant.core.database.dao.ChatAttachmentDao
import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import com.krisslin.androidaiassistant.core.database.dao.UserFactDao
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity

@Database(
    entities = [ChatMessageEntity::class, ChatAttachmentEntity::class, BotNotificationEntity::class, UserFactEntity::class],
    version = 3,
    exportSchema = false
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun chatMessageDao(): ChatMessageDao
    abstract fun chatAttachmentDao(): ChatAttachmentDao
    abstract fun botNotificationDao(): BotNotificationDao
    abstract fun userFactDao(): UserFactDao
}
