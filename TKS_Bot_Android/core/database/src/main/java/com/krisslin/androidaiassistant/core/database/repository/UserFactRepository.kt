package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.UserFactDao
import com.krisslin.androidaiassistant.core.database.AppDatabase
import com.krisslin.androidaiassistant.core.database.entity.MemorySyncCursorEntity
import com.krisslin.androidaiassistant.core.database.entity.MemorySeenEventEntity
import com.krisslin.androidaiassistant.core.database.entity.MemoryPermissionOperationEntity
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import androidx.room.withTransaction
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class UserFactRepository @Inject constructor(
    private val database: AppDatabase,
    private val userFactDao: UserFactDao
) {
    fun observeByUser(userId: String): Flow<List<UserFactEntity>> = userFactDao.observeByUser(userId)

    suspend fun upsertAll(facts: List<UserFactEntity>) {
        if (facts.isEmpty()) return
        userFactDao.upsertAll(facts)
    }

    suspend fun getLatestTimestamp(userId: String): Long = userFactDao.getLatestTimestamp(userId)

    suspend fun getLatestTimestamp(): Long = userFactDao.getLatestTimestampAny()

    suspend fun cursor(userId: String): Long = userFactDao.getCursor(userId) ?: 0L

    /** Event identity, fact projection and cursor move commit together. */
    suspend fun applyPage(userId: String, events: List<Pair<String, UserFactEntity>>, nextCursor: Long) {
        database.withTransaction {
            val currentCursor = cursor(userId)
            if (nextCursor < currentCursor) return@withTransaction
            for ((eventId, fact) in events) {
                if (fact.userId != userId || eventId.isBlank()) continue
                if (userFactDao.insertSeenEvent(MemorySeenEventEntity(eventId, userId)) == -1L) continue
                val current = userFactDao.getFact(fact.factId)
                if (current == null || current.revision < fact.revision ||
                    (current.revision == fact.revision && current.permissionVersion < fact.permissionVersion)
                ) userFactDao.upsert(fact)
            }
            userFactDao.putCursor(MemorySyncCursorEntity(userId, nextCursor))
        }
    }

    suspend fun fact(factId: String): UserFactEntity? = userFactDao.getFact(factId)

    suspend fun enqueuePermission(operation: MemoryPermissionOperationEntity) {
        userFactDao.enqueuePermission(operation)
    }

    suspend fun pendingPermissions(): List<MemoryPermissionOperationEntity> = userFactDao.pendingPermissions()

    suspend fun latestPermissionOperation(factId: String): MemoryPermissionOperationEntity? =
        userFactDao.latestPermissionOperation(factId)

    suspend fun markPermissionOperation(operationId: String, state: String) =
        userFactDao.markPermissionOperation(operationId, state)
}
