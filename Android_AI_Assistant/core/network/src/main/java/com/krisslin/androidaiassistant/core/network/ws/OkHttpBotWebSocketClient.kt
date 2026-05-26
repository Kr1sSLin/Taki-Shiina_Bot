package com.krisslin.androidaiassistant.core.network.ws

import com.krisslin.androidaiassistant.core.network.BuildConfig
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
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class OkHttpBotWebSocketClient @Inject constructor(
    private val okHttpClient: OkHttpClient
) : BotWebSocketClient {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val wsRef = AtomicReference<WebSocket?>(null)
    private val _events = MutableSharedFlow<WebSocketEvent>(extraBufferCapacity = 64)
    override val events = _events.asSharedFlow()
    private var heartbeatJob: Job? = null
    private var reconnectJob: Job? = null
    private var reconnectAttempts = 0
    private var manuallyClosed = false
    private var latestToken: String? = null
    private val reconnecting = AtomicBoolean(false)

    override fun connect(token: String) {
        latestToken = token
        manuallyClosed = false
        reconnectAttempts = 0
        reconnecting.set(false)
        openWebSocket(token)
    }

    private fun openWebSocket(token: String) {
        heartbeatJob?.cancel()
        wsRef.getAndSet(null)?.close(1000, "reconnect")
        val request = Request.Builder()
            .url("${BuildConfig.WS_BASE_URL}/ws/chat")
            .header("Authorization", "Bearer $token")
            .build()
        val webSocket = okHttpClient.newWebSocket(request, listener)
        wsRef.set(webSocket)
    }

    override fun disconnect() {
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
            if (!manuallyClosed) {
                openWebSocket(token)
            }
            reconnecting.set(false)
        }
    }

    private fun startHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (true) {
                delay(25_000L)
                val sent = send("""{"type":"ping","timestamp":${System.currentTimeMillis()}}""")
                if (!sent) {
                    scheduleReconnect()
                    break
                }
            }
        }
    }

    private val listener = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            reconnectAttempts = 0
            reconnectJob?.cancel()
            reconnecting.set(false)
            _events.tryEmit(WebSocketEvent.Connected)
            startHeartbeat()
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            _events.tryEmit(WebSocketEvent.Message(text))
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            heartbeatJob?.cancel()
            _events.tryEmit(WebSocketEvent.Failure(t))
            scheduleReconnect()
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            heartbeatJob?.cancel()
            _events.tryEmit(WebSocketEvent.Disconnected)
            scheduleReconnect()
        }
    }
}
