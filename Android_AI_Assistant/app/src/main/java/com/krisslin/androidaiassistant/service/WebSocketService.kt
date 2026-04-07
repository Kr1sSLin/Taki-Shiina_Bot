package com.krisslin.androidaiassistant.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.google.gson.Gson
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser
import com.krisslin.androidaiassistant.core.network.ws.WebSocketEvent
import com.krisslin.androidaiassistant.core.push.AppNotifier
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import javax.inject.Inject

@AndroidEntryPoint
class WebSocketService : Service() {

    companion object {
        private const val NOTIFICATION_ID = 1001
        private const val CHANNEL_ID = "websocket_service"
        private const val CHANNEL_NAME = "后台服务"
        const val ACTION_START = "com.krisslin.androidaiassistant.ACTION_START"
        const val ACTION_STOP = "com.krisslin.androidaiassistant.ACTION_STOP"
    }

    @Inject
    lateinit var webSocketClient: BotWebSocketClient

    @Inject
    lateinit var authRepository: AuthRepository

    @Inject
    lateinit var appNotifier: AppNotifier

    @Inject
    lateinit var gson: Gson

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val messageParser by lazy { IncomingMessageParser(gson) }
    private val binder = LocalBinder()

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForeground(NOTIFICATION_ID, createNotification())
        appNotifier.ensureChannels()
        connectWebSocket()
        observeMessages()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                // 已在onCreate中处理
            }
            ACTION_STOP -> {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder {
        return binder
    }

    override fun onDestroy() {
        super.onDestroy()
        webSocketClient.disconnect()
    }

    private fun connectWebSocket() {
        val token = authRepository.getAccessToken() ?: ""
        webSocketClient.connect(token)
    }

    private fun observeMessages() {
        serviceScope.launch {
            webSocketClient.events.collect { event ->
                if (event is WebSocketEvent.Message) {
                    handleMessage(event.text)
                }
            }
        }
    }

    private fun handleMessage(raw: String) {
        when (val msg = messageParser.parse(raw)) {
            is IncomingMessage.Reply -> {
                appNotifier.showChatMessage(msg.payload.content)
            }
            is IncomingMessage.ReplyStream -> {
                if (msg.payload.done) {
                    val content = msg.payload.finalContent ?: ""
                    if (content.isNotBlank()) {
                        appNotifier.showChatMessage(content)
                    }
                }
            }
            is IncomingMessage.BotError -> {
                appNotifier.showBotError(msg.payload.errorCode, msg.payload.message)
            }
            else -> { /* Typing / AuthExpired / Unknown → 不推通知 */ }
        }
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                CHANNEL_NAME,
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "保持WebSocket连接以接收消息"
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun createNotification(): Notification {
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("立希")
            .setContentText("后台运行中")
            .setSmallIcon(android.R.drawable.ic_dialog_email)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    inner class LocalBinder : Binder() {
        fun getService(): WebSocketService = this@WebSocketService
    }
}
