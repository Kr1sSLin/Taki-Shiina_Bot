package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.UserFactDao
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class UserFactRepository @Inject constructor(
    private val userFactDao: UserFactDao
) {
    fun observeByUser(userId: String): Flow<List<UserFactEntity>> = userFactDao.observeByUser(userId)

    suspend fun upsertAll(facts: List<UserFactEntity>) {
        if (facts.isEmpty()) return
        userFactDao.upsertAll(facts)
    }

    suspend fun getLatestTimestamp(userId: String): Long = userFactDao.getLatestTimestamp(userId)

    suspend fun getLatestTimestamp(): Long = userFactDao.getLatestTimestampAny()
}
