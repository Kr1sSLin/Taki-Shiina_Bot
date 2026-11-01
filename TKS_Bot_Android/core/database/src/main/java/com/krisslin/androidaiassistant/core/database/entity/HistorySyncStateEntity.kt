package com.krisslin.androidaiassistant.core.database.entity

import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "history_sync_state")
data class HistorySyncStateEntity(@PrimaryKey val key: String, val value: String)
