package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.krisslin.androidaiassistant.core.database.entity.UserProgressEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface UserProgressDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(progress: UserProgressEntity)

    @Query("SELECT * FROM user_progress WHERE user_id = :userId LIMIT 1")
    suspend fun get(userId: String): UserProgressEntity?

    @Query("SELECT * FROM user_progress WHERE user_id = :userId LIMIT 1")
    fun observe(userId: String): Flow<UserProgressEntity?>

    @Query("DELETE FROM user_progress")
    suspend fun clear()
}
