package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "memory_sync_cursor")
data class MemorySyncCursorEntity(
    @PrimaryKey @ColumnInfo(name = "user_id") val userId: String,
    val cursor: Long
)

@Entity(tableName = "memory_seen_events")
data class MemorySeenEventEntity(
    @PrimaryKey @ColumnInfo(name = "event_id") val eventId: String,
    @ColumnInfo(name = "user_id") val userId: String
)

@Entity(tableName = "memory_permission_queue")
data class MemoryPermissionOperationEntity(
    @PrimaryKey @ColumnInfo(name = "operation_id") val operationId: String,
    @ColumnInfo(name = "user_id") val userId: String,
    @ColumnInfo(name = "fact_id") val factId: String,
    val enabled: Boolean,
    @ColumnInfo(name = "expected_revision") val expectedRevision: Long,
    val state: String = "pending"
)
