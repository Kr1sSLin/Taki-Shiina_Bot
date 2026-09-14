package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.BotNotificationDao
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import kotlinx.coroutines.flow.Flow
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class BotNotificationRepository @Inject constructor(
    private val botNotificationDao: BotNotificationDao
) {
    fun observeAll(): Flow<List<BotNotificationEntity>> = botNotificationDao.observeAll()

    fun observeUnreadCount(): Flow<Int> = botNotificationDao.observeUnreadCount()

    suspend fun addError(errorCode: String, message: String, timestamp: Long) {
        val id = "bot_err_${timestamp}_${UUID.randomUUID()}"
        botNotificationDao.upsert(
            BotNotificationEntity(
                notificationId = id,
                errorCode = errorCode,
                message = message,
                isRead = 0,
                timestamp = timestamp
            )
        )
    }

    suspend fun markAsRead(notificationId: String) {
        botNotificationDao.markAsRead(notificationId)
    }

    suspend fun markAllAsRead() {
        botNotificationDao.markAllAsRead()
    }
}
