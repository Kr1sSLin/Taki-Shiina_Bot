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
    val timestamp: Long
)
