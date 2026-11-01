package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

@Entity(
    tableName = "user_facts",
    indices = [
        Index(value = ["user_id", "timestamp"])
    ]
)
data class UserFactEntity(
    @PrimaryKey @ColumnInfo(name = "fact_id") val factId: String,
    @ColumnInfo(name = "user_id") val userId: String,
    val fact: String,
    val timestamp: Long,
    @ColumnInfo(defaultValue = "0") val revision: Long = 0,
    @ColumnInfo(name = "revision_id") val revisionId: String? = null,
    @ColumnInfo(name = "permission_version", defaultValue = "0") val permissionVersion: Long = 0,
    @ColumnInfo(defaultValue = "1") val enabled: Boolean = true,
    @ColumnInfo(defaultValue = "'candidate'") val stage: String = "candidate",
    @ColumnInfo(name = "truth_status", defaultValue = "'pending'") val truthStatus: String = "pending",
    @ColumnInfo(defaultValue = "0.0") val score: Double = 0.0,
    @ColumnInfo(name = "source_message_id") val sourceMessageId: String? = null,
    @ColumnInfo(name = "temporal_validity") val temporalValidity: String? = null
)
