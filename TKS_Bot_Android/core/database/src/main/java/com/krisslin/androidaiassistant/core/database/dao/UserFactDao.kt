package com.krisslin.androidaiassistant.core.database.dao

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.entity.MemorySyncCursorEntity
import com.krisslin.androidaiassistant.core.database.entity.MemorySeenEventEntity
import com.krisslin.androidaiassistant.core.database.entity.MemoryPermissionOperationEntity
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

    @Query("SELECT * FROM user_facts WHERE fact_id = :factId LIMIT 1")
    suspend fun getFact(factId: String): UserFactEntity?

    @Query("SELECT cursor FROM memory_sync_cursor WHERE user_id = :userId")
    suspend fun getCursor(userId: String): Long?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun putCursor(cursor: MemorySyncCursorEntity)

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertSeenEvent(event: MemorySeenEventEntity): Long

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun enqueuePermission(operation: MemoryPermissionOperationEntity): Long

    @Query("SELECT * FROM memory_permission_queue WHERE state = 'pending' ORDER BY rowid")
    suspend fun pendingPermissions(): List<MemoryPermissionOperationEntity>

    @Query("SELECT * FROM memory_permission_queue WHERE fact_id = :factId ORDER BY rowid DESC LIMIT 1")
    suspend fun latestPermissionOperation(factId: String): MemoryPermissionOperationEntity?

    @Query("UPDATE memory_permission_queue SET state = :state WHERE operation_id = :operationId")
    suspend fun markPermissionOperation(operationId: String, state: String)
}
