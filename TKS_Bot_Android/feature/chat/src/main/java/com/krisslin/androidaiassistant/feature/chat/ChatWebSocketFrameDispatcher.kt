package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.Gson
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser

/**
 * Normalizes raw WebSocket frames and dispatches typed events through a deliberately narrow sink.
 * It owns no UI, repository, coroutine, or ViewModel state.
 */
internal class ChatWebSocketFrameDispatcher(
    gson: Gson,
    private val sink: Sink
) {
    interface Sink {
        suspend fun onReply(message: IncomingMessage.Reply)
        suspend fun onReplyStream(message: IncomingMessage.ReplyStream)
        suspend fun onBotError(message: IncomingMessage.BotError)
        suspend fun onMemoryFactCreated(message: IncomingMessage.MemoryFactCreated)
        suspend fun onUserEcho(message: IncomingMessage.UserEcho)
        fun onTyping(message: IncomingMessage.Typing)
        fun onPointsChanged(message: IncomingMessage.PointsChanged)
        fun onLevelChanged(message: IncomingMessage.LevelChanged)
        fun onStreakWarning(message: IncomingMessage.StreakWarning)
        fun onMakeupCardChanged(message: IncomingMessage.MakeupCardChanged)
        suspend fun onAuthExpired()
    }

    private val parser = IncomingMessageParser(gson)

    suspend fun dispatch(raw: String) {
        when (val message = parser.parse(raw)) {
            is IncomingMessage.Reply -> sink.onReply(message)
            is IncomingMessage.ReplyStream -> sink.onReplyStream(message)
            is IncomingMessage.BotError -> sink.onBotError(message)
            is IncomingMessage.MemoryFactCreated -> sink.onMemoryFactCreated(message)
            is IncomingMessage.UserEcho -> sink.onUserEcho(message)
            is IncomingMessage.Typing -> sink.onTyping(message)
            is IncomingMessage.PointsChanged -> sink.onPointsChanged(message)
            is IncomingMessage.LevelChanged -> sink.onLevelChanged(message)
            is IncomingMessage.StreakWarning -> sink.onStreakWarning(message)
            is IncomingMessage.MakeupCardChanged -> sink.onMakeupCardChanged(message)
            is IncomingMessage.AuthExpired -> sink.onAuthExpired()
            is IncomingMessage.Unknown -> Unit
        }
    }
}
