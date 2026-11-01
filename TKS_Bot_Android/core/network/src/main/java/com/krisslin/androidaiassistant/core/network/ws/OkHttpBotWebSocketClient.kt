package com.krisslin.androidaiassistant.core.network.ws

import com.krisslin.androidaiassistant.core.network.BuildConfig
import com.google.gson.JsonParser
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class OkHttpBotWebSocketClient @Inject constructor(
    private val okHttpClient: OkHttpClient
) : BotWebSocketClient {

    private val wsClient = okHttpClient.newBuilder().pingInterval(25, TimeUnit.SECONDS).build()
    @Volatile private var generation = 0L
    private val pendingPings = ArrayDeque<Long>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val wsRef = AtomicReference<WebSocket?>(null)
    private val _events = MutableSharedFlow<WebSocketEvent>(extraBufferCapacity = 64)
    override val events = _events.asSharedFlow()
    private var heartbeatJob: Job? = null
    private var reconnectJob: Job? = null
    private var reconnectAttempts = 0
    @Volatile private var manuallyClosed = false
    private var latestToken: String? = null
    private val reconnecting = AtomicBoolean(false)

    @Synchronized
    override fun connect(token: String) {
        reconnectJob?.cancel()
        latestToken = token
        manuallyClosed = false
        reconnectAttempts = 0
        reconnecting.set(false)
        openWebSocket(token)
    }

    @Synchronized
    private fun openWebSocket(token: String) {
        val currentGeneration = ++generation
        pendingPings.clear()
        heartbeatJob?.cancel()
        wsRef.getAndSet(null)?.close(1000, "reconnect")
        val request = Request.Builder()
            .url("${BuildConfig.WS_BASE_URL}/ws/chat")
            .header("Authorization", "Bearer $token")
            .build()
        val webSocket = wsClient.newWebSocket(request, listener(currentGeneration))
        if (generation == currentGeneration) wsRef.set(webSocket)
    }

    @Synchronized
    override fun disconnect() {
        generation += 1
        pendingPings.clear()
        manuallyClosed = true
        reconnecting.set(false)
        heartbeatJob?.cancel()
        reconnectJob?.cancel()
        wsRef.getAndSet(null)?.close(1000, "client disconnect")
        _events.tryEmit(WebSocketEvent.Disconnected)
    }

    override fun send(rawJson: String): Boolean {
        return wsRef.get()?.send(rawJson) ?: false
    }

    private fun scheduleReconnect() {
        val token = latestToken ?: return
        if (manuallyClosed || reconnectAttempts >= 15) return
        if (!reconnecting.compareAndSet(false, true)) return
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            val backoffSeconds = (1 shl reconnectAttempts).coerceAtMost(60)
            reconnectAttempts += 1
            delay(backoffSeconds * 1000L)
            reconnecting.set(false)
            if (!manuallyClosed) {
                openWebSocket(token)
            }
        }
    }

    private fun startHeartbeat(webSocket: WebSocket, currentGeneration: Long) {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (true) {
                delay(25_000L)
                if (generation != currentGeneration || !heartbeatTick(webSocket)) break
            }
        }
    }

    @Synchronized
    internal fun heartbeatTick(webSocket: WebSocket): Boolean {
        if (wsRef.get() !== webSocket) return false
        if (pendingPings.size >= 3) {
            failConnection(webSocket)
            return false
        }
        val timestamp = maxOf(System.currentTimeMillis(), (pendingPings.lastOrNull() ?: 0) + 1)
        pendingPings.addLast(timestamp)
        if (!webSocket.send("""{"type":"ping","payload":{"timestamp":$timestamp}}""")) {
            failConnection(webSocket)
            return false
        }
        return true
    }

    private fun failConnection(webSocket: WebSocket) {
        generation += 1
        wsRef.compareAndSet(webSocket, null)
        pendingPings.clear()
        webSocket.cancel()
        _events.tryEmit(WebSocketEvent.Disconnected)
        scheduleReconnect()
    }

    @Synchronized
    private fun observePong(text: String) {
        val frame = runCatching { JsonParser.parseString(text).asJsonObject }.getOrNull() ?: return
        if (frame.get("type")?.toString() != "\"pong\"" || pendingPings.isEmpty()) return
        if (!frame.has("pingTimestamp")) {
            pendingPings.clear()
        } else {
            val echo = frame.get("pingTimestamp")
            if (echo.isJsonPrimitive && echo.asJsonPrimitive.isNumber && pendingPings.any { echo.asString == it.toString() }) {
                pendingPings.clear()
            }
        }
    }

    private fun listener(currentGeneration: Long) = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) = synchronized(this@OkHttpBotWebSocketClient) {
            if (generation != currentGeneration) return
            wsRef.set(webSocket)
            reconnectAttempts = 0
            reconnectJob?.cancel()
            reconnecting.set(false)
            _events.tryEmit(WebSocketEvent.Connected)
            startHeartbeat(webSocket, currentGeneration)
        }

        override fun onMessage(webSocket: WebSocket, text: String): Unit = synchronized(this@OkHttpBotWebSocketClient) {
            if (generation != currentGeneration || wsRef.get() !== webSocket) return
            observePong(text)
            _events.tryEmit(WebSocketEvent.Message(text))
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) = synchronized(this@OkHttpBotWebSocketClient) {
            if (generation != currentGeneration) return
            webSocket.close(code, reason)
            Unit
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = synchronized(this@OkHttpBotWebSocketClient) {
            if (generation != currentGeneration) return
            generation += 1
            wsRef.compareAndSet(webSocket, null)
            pendingPings.clear()
            heartbeatJob?.cancel()
            val statusCode = response?.code
            _events.tryEmit(WebSocketEvent.Failure(t, statusCode))
            if (statusCode != 401 && statusCode != 403) scheduleReconnect()
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = synchronized(this@OkHttpBotWebSocketClient) {
            if (generation != currentGeneration) return
            generation += 1
            wsRef.compareAndSet(webSocket, null)
            pendingPings.clear()
            heartbeatJob?.cancel()
            _events.tryEmit(WebSocketEvent.Disconnected)
            if (code != 4001) scheduleReconnect()
        }
    }
}
