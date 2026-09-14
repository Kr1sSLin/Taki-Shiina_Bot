package com.krisslin.androidaiassistant.core.database.repository

import com.krisslin.androidaiassistant.core.database.dao.UserProgressDao
import com.krisslin.androidaiassistant.core.database.entity.UserProgressEntity
import kotlinx.coroutines.flow.Flow
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 积分/等级本地缓存仓库（PRD §3.1 / US-8）。
 *
 * **以后端为准**：仅在拿到后端数据（REST 查询结果或 WS 事件）时写入，
 * 本地读取只用于离线展示与首屏秒开，不做任何本地累加计算。
 */
@Singleton
class UserProgressRepository @Inject constructor(
    private val dao: UserProgressDao
) {
    companion object {
        /**
         * 本应用为单账号部署（后端 `APP_USER_ID` 默认 `default-user`），
         * 客户端缓存统一使用该键，与后端账号维度保持一致。
         */
        const val DEFAULT_USER_ID = "default-user"
    }

    fun observe(userId: String = DEFAULT_USER_ID): Flow<UserProgressEntity?> = dao.observe(userId)

    suspend fun get(userId: String = DEFAULT_USER_ID): UserProgressEntity? = dao.get(userId)

    suspend fun upsert(progress: UserProgressEntity) = dao.upsert(progress)

    suspend fun clear() = dao.clear()

    /** 用后端余额刷新缓存（收到 points.changed 时调用）。 */
    suspend fun applyBalance(balance: Int, userId: String = DEFAULT_USER_ID) {
        val current = dao.get(userId)
        val base = current ?: UserProgressEntity(
            userId = userId,
            balance = balance,
            levelCode = "NONE",
            levelName = "",
            continuousDays = 0,
            nextLevelName = null,
            nextLevelThresholdDays = null,
            daysToNextLevel = null,
            availableMakeupCards = 0,
            gapDays = null,
            breakDeadlineDate = null,
            highestLevelCode = null,
            updatedAt = System.currentTimeMillis()
        )
        dao.upsert(base.copy(balance = balance, updatedAt = System.currentTimeMillis()))
    }

    /** 用后端等级字段刷新缓存（收到 level.changed 或等级查询结果时调用）。 */
    suspend fun applyLevel(
        levelCode: String,
        levelName: String,
        continuousDays: Int,
        nextLevelName: String?,
        nextLevelThresholdDays: Int?,
        daysToNextLevel: Int?,
        highestLevelCode: String?,
        gapDays: Int?,
        breakDeadlineDate: String?,
        userId: String = DEFAULT_USER_ID
    ) {
        val current = dao.get(userId)
        val base = current ?: UserProgressEntity(
            userId = userId,
            balance = 0,
            levelCode = levelCode,
            levelName = levelName,
            continuousDays = continuousDays,
            nextLevelName = nextLevelName,
            nextLevelThresholdDays = nextLevelThresholdDays,
            daysToNextLevel = daysToNextLevel,
            availableMakeupCards = 0,
            gapDays = gapDays,
            breakDeadlineDate = breakDeadlineDate,
            highestLevelCode = highestLevelCode,
            updatedAt = System.currentTimeMillis()
        )
        dao.upsert(
            base.copy(
                levelCode = levelCode,
                levelName = levelName,
                continuousDays = continuousDays,
                nextLevelName = nextLevelName,
                nextLevelThresholdDays = nextLevelThresholdDays,
                daysToNextLevel = daysToNextLevel,
                highestLevelCode = highestLevelCode ?: base.highestLevelCode,
                gapDays = gapDays,
                breakDeadlineDate = breakDeadlineDate,
                updatedAt = System.currentTimeMillis()
            )
        )
    }

    /** 补签卡库存刷新（收到 makeup_card.changed 时调用）。 */
    suspend fun applyMakeupCardCount(available: Int, userId: String = DEFAULT_USER_ID) {
        val current = dao.get(userId) ?: return
        dao.upsert(current.copy(availableMakeupCards = available, updatedAt = System.currentTimeMillis()))
    }
}
