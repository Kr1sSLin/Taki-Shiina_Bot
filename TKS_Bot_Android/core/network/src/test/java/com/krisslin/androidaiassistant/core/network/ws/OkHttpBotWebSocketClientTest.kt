package com.krisslin.androidaiassistant.core.network.ws

import com.google.gson.JsonParser
import kotlinx.coroutines.Job
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.atomic.AtomicReference

class OkHttpBotWebSocketClientTest {
    private class Socket : WebSocket {
        val frames = mutableListOf<String>()
        var cancelled = false
        override fun request() = Request.Builder().url("http://127.0.0.1/").build()
        override fun queueSize() = 0L
        override fun send(text: String): Boolean { frames += text; return true }
        override fun send(bytes: ByteString) = true
        override fun close(code: Int, reason: String?) = true
        override fun cancel() { cancelled = true }
    }

    private fun field(client: OkHttpBotWebSocketClient, name: String) =
        client.javaClass.getDeclaredField(name).apply { isAccessible = true }

    private fun listener(client: OkHttpBotWebSocketClient, generation: Long): WebSocketListener =
        client.javaClass.getDeclaredMethod("listener", Long::class.javaPrimitiveType).apply { isAccessible = true }
            .invoke(client, generation) as WebSocketListener

    @Suppress("UNCHECKED_CAST")
    private fun socketRef(client: OkHttpBotWebSocketClient) = field(client, "wsRef").get(client) as AtomicReference<WebSocket?>

    @Test fun matchingPongResetsButStaleCallbacksCannotKeepNewConnectionAlive() {
        val client = OkHttpBotWebSocketClient(OkHttpClient())
        val socket = Socket()
        try {
            socketRef(client).set(socket)
            val current = listener(client, 0)
            val old = listener(client, -1)
            assertTrue(client.heartbeatTick(socket))
            val ping = JsonParser.parseString(socket.frames.last()).asJsonObject.getAsJsonObject("payload").get("timestamp").asLong
            current.onMessage(socket, """{"type":"pong","pingTimestamp":$ping}""")
            repeat(3) { assertTrue(client.heartbeatTick(socket)) }
            old.onMessage(Socket(), """{"type":"pong"}""")
            old.onFailure(Socket(), IllegalStateException("old socket"), null)
            old.onClosed(Socket(), 1006, "old socket")
            current.onMessage(socket, """{"type":"pong","pingTimestamp":-123}""")
            assertSame(socket, socketRef(client).get())
            assertFalse(client.heartbeatTick(socket))
            assertTrue(socket.cancelled)
            assertNull(socketRef(client).get())
        } finally { client.disconnect() }
    }

    @Test fun legacyPongIsAcceptedAndMissingPongSchedulesReconnect() {
        val client = OkHttpBotWebSocketClient(OkHttpClient())
        val socket = Socket()
        try {
            field(client, "latestToken").set(client, "synthetic-token")
            socketRef(client).set(socket)
            repeat(3) { assertTrue(client.heartbeatTick(socket)) }
            listener(client, 0).onMessage(socket, """{"type":"pong","timestamp":123}""")
            repeat(3) { assertTrue(client.heartbeatTick(socket)) }
            assertFalse(client.heartbeatTick(socket))
            assertTrue((field(client, "reconnectJob").get(client) as Job).isActive)
            assertFalse(client.heartbeatTick(socket))
            assertTrue(socket.cancelled)
            val wsClient = field(client, "wsClient").get(client) as OkHttpClient
            assertEquals(25_000, wsClient.pingIntervalMillis)
        } finally { client.disconnect() }
    }
}
