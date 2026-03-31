package com.krisslin.androidaiassistant.core.push

data class BotErrorEvent(
    val errorCode: String,
    val message: String,
    val timestamp: Long
)