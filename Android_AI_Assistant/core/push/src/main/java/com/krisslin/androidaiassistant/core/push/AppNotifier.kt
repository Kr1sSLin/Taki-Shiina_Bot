package com.krisslin.androidaiassistant.core.push

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class AppNotifier @Inject constructor(
    @ApplicationContext private val context: Context
) {
    companion object {
        const val CHANNEL_CHAT = "chat_messages"
        const val CHANNEL_REMINDER = "chat_reminders"
    }

    fun ensureChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val chat = NotificationChannel(
            CHANNEL_CHAT,
            "聊天通知",
            NotificationManager.IMPORTANCE_DEFAULT
        )
        val reminder = NotificationChannel(
            CHANNEL_REMINDER,
            "提醒通知",
            NotificationManager.IMPORTANCE_HIGH
        )
        manager.createNotificationChannel(chat)
        manager.createNotificationChannel(reminder)
    }

    fun showBotError(errorCode: String, message: String) {
        if (!canPostNotifications()) return
        val notification = NotificationCompat.Builder(context, CHANNEL_CHAT)
            .setSmallIcon(android.R.drawable.stat_notify_error)
            .setContentTitle("Bot 错误：$errorCode")
            .setContentText(message)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message))
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify((System.currentTimeMillis() % Int.MAX_VALUE).toInt(), notification)
    }

    fun showReminder(text: String, target: String) {
        if (!canPostNotifications()) return
        val body = "提醒时间：$target\n$text"
        val notification = NotificationCompat.Builder(context, CHANNEL_REMINDER)
            .setSmallIcon(android.R.drawable.ic_popup_reminder)
            .setContentTitle("消息提醒")
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify((System.currentTimeMillis() % Int.MAX_VALUE).toInt(), notification)
    }

    private fun canPostNotifications(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return true
        return ContextCompat.checkSelfPermission(
            context,
            Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
    }
}
