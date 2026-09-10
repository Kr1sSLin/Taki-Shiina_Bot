package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * 用户积分/等级本地缓存（PRD §3.1：`core:database` 新增 Room 表，**以后端为准，本地仅做缓存**）。
 *
 * 后端是唯一数据源（Single Source of Truth）：登录/重连/收到 WS 事件时以后端数据覆盖本表，
 * 离线时用本表做可见展示。断签只影响等级字段，不会清零 [balance]（PRD FR-17）。
 */
@Entity(tableName = "user_progress")
data class UserProgressEntity(
    @PrimaryKey @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "balance") val balance: Int,
    @ColumnInfo(name = "level_code") val levelCode: String,
    @ColumnInfo(name = "level_name") val levelName: String,
    @ColumnInfo(name = "continuous_days") val continuousDays: Int,
    @ColumnInfo(name = "next_level_name") val nextLevelName: String?,
    @ColumnInfo(name = "next_level_threshold_days") val nextLevelThresholdDays: Int?,
    @ColumnInfo(name = "days_to_next_level") val daysToNextLevel: Int?,
    @ColumnInfo(name = "available_makeup_cards") val availableMakeupCards: Int,
    @ColumnInfo(name = "gap_days") val gapDays: Int?,
    @ColumnInfo(name = "break_deadline_date") val breakDeadlineDate: String?,
    @ColumnInfo(name = "highest_level_code") val highestLevelCode: String?,
    @ColumnInfo(name = "updated_at") val updatedAt: Long
)
