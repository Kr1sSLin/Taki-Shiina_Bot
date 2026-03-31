package com.krisslin.androidaiassistant.core.network.ws

import kotlinx.coroutines.flow.Flow

interface BotWebSocketClient {
    fun connect(token: String)
    fun disconnect()
    fun send(rawJson: String): Boolean
    val events: Flow<WebSocketEvent>
}

sealed interface WebSocketEvent {
    data object Connected : WebSocketEvent
    data object Disconnected : WebSocketEvent
    data class Message(val text: String) : WebSocketEvent
    data class Failure(val throwable: Throwable) : WebSocketEvent
}
