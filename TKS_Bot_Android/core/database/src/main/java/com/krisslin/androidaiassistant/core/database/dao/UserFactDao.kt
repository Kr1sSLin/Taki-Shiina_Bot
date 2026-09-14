package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface UserFactDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(fact: UserFactEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(facts: List<UserFactEntity>)

    @Query("SELECT * FROM user_facts WHERE user_id = :userId ORDER BY timestamp DESC")
    fun observeByUser(userId: String): Flow<List<UserFactEntity>>

    @Query("SELECT COALESCE(MAX(timestamp), 0) FROM user_facts WHERE user_id = :userId")
    suspend fun getLatestTimestamp(userId: String): Long

    @Query("SELECT COALESCE(MAX(timestamp), 0) FROM user_facts")
    suspend fun getLatestTimestampAny(): Long
}
