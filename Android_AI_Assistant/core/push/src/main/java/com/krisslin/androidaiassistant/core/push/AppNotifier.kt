package com.krisslin.androidaiassistant.core.push

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
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
        const val CHANNEL_GREETING = "greeting_messages"

        // 固定通知 ID：保证同类通知替换展示、可被定向取消（1001 为前台服务常驻通知，勿动）
        const val NOTIFICATION_ID_CHAT = 1002
        const val NOTIFICATION_ID_GREETING = 1003
        const val NOTIFICATION_ID_ERROR = 1004
        const val NOTIFICATION_ID_REMINDER = 1005

        // 通知未被打点/打开时，系统超时自动移除（兜底防常驻）
        private const val AUTO_DISMISS_TIMEOUT_MS = 10 * 1000L
    }

    fun ensureChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val chat = NotificationChannel(
            CHANNEL_CHAT,
            "聊天通知",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            enableVibration(true)
            vibrationPattern = longArrayOf(0, 300, 200, 300)
        }
        val reminder = NotificationChannel(
            CHANNEL_REMINDER,
            "提醒通知",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            enableVibration(true)
            vibrationPattern = longArrayOf(0, 300, 200, 300)
        }
        val greeting = NotificationChannel(
            CHANNEL_GREETING,
            "问候通知",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            enableVibration(true)
            vibrationPattern = longArrayOf(0, 300, 200, 300)
        }
        manager.createNotificationChannel(chat)
        manager.createNotificationChannel(reminder)
        manager.createNotificationChannel(greeting)
    }

    private fun launchIntent(): PendingIntent {
        val intent = context.packageManager.getLaunchIntentForPackage(context.packageName)
            ?: Intent()
        intent.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP
        return PendingIntent.getActivity(
            context, 0, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    fun showBotError(errorCode: String, message: String) {
        if (!canPostNotifications()) return
        val pi = launchIntent()
        val notification = NotificationCompat.Builder(context, CHANNEL_CHAT)
            .setSmallIcon(android.R.drawable.stat_notify_error)
            .setContentTitle("Bot 错误：$errorCode")
            .setContentText(message)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .setTimeoutAfter(AUTO_DISMISS_TIMEOUT_MS)
            .build()
        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID_ERROR, notification)
    }

    fun showReminder(text: String, target: String) {
        if (!canPostNotifications()) return
        val pi = launchIntent()
        val body = "提醒时间：$target\n$text"
        val notification = NotificationCompat.Builder(context, CHANNEL_REMINDER)
            .setSmallIcon(android.R.drawable.ic_popup_reminder)
            .setContentTitle("消息提醒")
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .setTimeoutAfter(AUTO_DISMISS_TIMEOUT_MS)
            .build()
        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID_REMINDER, notification)
    }

    fun showChatMessage(content: String) {
        if (!canPostNotifications()) return
        val pi = launchIntent()
        val notification = NotificationCompat.Builder(context, CHANNEL_CHAT)
            .setSmallIcon(android.R.drawable.ic_dialog_email)
            .setContentTitle("立希")
            .setContentText(content.take(50))
            .setStyle(NotificationCompat.BigTextStyle().bigText(content))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .setTimeoutAfter(AUTO_DISMISS_TIMEOUT_MS)
            .build()
        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID_CHAT, notification)
    }

    fun showGreeting(scenario: String?, content: String) {
        if (!canPostNotifications()) return
        val pi = launchIntent()
        val title = when (scenario) {
            "morning" -> "立希的早安"
            "night" -> "立希的深夜问候"
            else -> "立希的问候"
        }
        val notification = NotificationCompat.Builder(context, CHANNEL_GREETING)
            .setSmallIcon(android.R.drawable.ic_dialog_email)
            .setContentTitle(title)
            .setContentText(content.take(50))
            .setStyle(NotificationCompat.BigTextStyle().bigText(content))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .setTimeoutAfter(AUTO_DISMISS_TIMEOUT_MS)
            .build()
        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID_GREETING, notification)
    }

    /**
     * 取消所有消息类通知（App 回到前台时调用）。
     * 注意不使用 cancelAll()：避免误删 WebSocketService 的前台常驻通知（1001）。
     */
    fun dismissAll() {
        val manager = NotificationManagerCompat.from(context)
        manager.cancel(NOTIFICATION_ID_CHAT)
        manager.cancel(NOTIFICATION_ID_GREETING)
        manager.cancel(NOTIFICATION_ID_ERROR)
        manager.cancel(NOTIFICATION_ID_REMINDER)
    }

    private fun canPostNotifications(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return true
        return ContextCompat.checkSelfPermission(
            context,
            Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
    }
}
