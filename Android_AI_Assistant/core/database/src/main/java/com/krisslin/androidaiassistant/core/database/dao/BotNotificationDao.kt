package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface BotNotificationDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(notification: BotNotificationEntity)

    @Query("SELECT * FROM bot_notifications ORDER BY timestamp DESC")
    fun observeAll(): Flow<List<BotNotificationEntity>>
}