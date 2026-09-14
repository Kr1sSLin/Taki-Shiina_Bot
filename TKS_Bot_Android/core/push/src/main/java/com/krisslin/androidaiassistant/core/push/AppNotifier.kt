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
        // 互动积分 · 等级体系（PRD FR-16 升级庆祝 / FR-18 断签提前提醒）
        const val CHANNEL_PROGRESS = "progress_updates"

        // 固定通知 ID：保证同类通知替换展示、可被定向取消（1001 为前台服务常驻通知，勿动）
        const val NOTIFICATION_ID_CHAT = 1002
        const val NOTIFICATION_ID_GREETING = 1003
        const val NOTIFICATION_ID_ERROR = 1004
        const val NOTIFICATION_ID_REMINDER = 1005
        const val NOTIFICATION_ID_LEVEL = 1006
        const val NOTIFICATION_ID_STREAK = 1007
        const val NOTIFICATION_ID_PROGRESS = 1008

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
        val progress = NotificationChannel(
            CHANNEL_PROGRESS,
            "陪伴与等级",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            enableVibration(true)
            vibrationPattern = longArrayOf(0, 300, 200, 300)
        }
        manager.createNotificationChannel(chat)
        manager.createNotificationChannel(reminder)
        manager.createNotificationChannel(greeting)
        manager.createNotificationChannel(progress)
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
     * 等级变化通知（PRD FR-16 / EDGE-11 / EDGE-8：推送给该账号全部在线设备）。
     *
     * `changeType` 区分文案：首次达成走升级庆祝；补签回溯挽回走克制的“已恢复”；
     * 断签回落明确告知等级已重置，并提示补签卡可挽回。
     */
    fun showLevelChanged(changeType: String?, levelName: String, continuousDays: Int) {
        if (!canPostNotifications()) return
        val title: String
        val body: String
        when (changeType) {
            "UPGRADE" -> {
                title = "🎉 升级啦！"
                body = "陪立希的第 $continuousDays 天，解锁新称号「$levelName」。"
            }
            "RESTORE" -> {
                title = "等级已恢复"
                body = "补签成功，称号「$levelName」回来了（连续陪伴 $continuousDays 天）。"
            }
            "RESET" -> {
                title = "连续陪伴中断"
                body = "等级已重置为初始状态，积分不受影响；使用补签卡可以挽回。"
            }
            else -> {
                title = "陪伴进度更新"
                body = "当前称号「$levelName」，连续陪伴 $continuousDays 天。"
            }
        }
        post(NOTIFICATION_ID_LEVEL, title, body)
    }

    /** 断签提前提醒（PRD FR-18：连续 3～4 天未对话，避免因疏忽丢失等级）。 */
    fun showStreakWarning(levelName: String, remainingDays: Int, deadlineDate: String?) {
        if (!canPostNotifications()) return
        val levelText = if (levelName.isBlank()) "当前等级" else "「$levelName」"
        val deadline = if (deadlineDate.isNullOrBlank()) "" else "\n请在 $deadlineDate 前和立希说句话。"
        post(
            NOTIFICATION_ID_STREAK,
            "⚠️ 立希还在等你",
            "已经好几天没聊天了，$levelText 将在 $remainingDays 天后归零。$deadline"
        )
    }

    /** 补签卡发放/使用提示（PRD FR-19 / FR-21）。 */
    fun showMakeupCardChanged(reason: String?, available: Int, maxAvailable: Int) {
        if (!canPostNotifications()) return
        val body = if (reason == "USED") {
            "已使用 1 张补签卡，剩余 $available/$maxAvailable 张。"
        } else {
            "本月补签卡已到账，当前可用 $available/$maxAvailable 张（可跨月结转）。"
        }
        post(NOTIFICATION_ID_PROGRESS, "补签卡", body)
    }

    private fun post(id: Int, title: String, body: String) {
        val pi = launchIntent()
        val notification = NotificationCompat.Builder(context, CHANNEL_PROGRESS)
            .setSmallIcon(android.R.drawable.btn_star_big_on)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setCategory(NotificationCompat.CATEGORY_SOCIAL)
            .setContentIntent(pi)
            .setAutoCancel(true)
            .setTimeoutAfter(AUTO_DISMISS_TIMEOUT_MS)
            .build()
        NotificationManagerCompat.from(context).notify(id, notification)
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
        manager.cancel(NOTIFICATION_ID_LEVEL)
        manager.cancel(NOTIFICATION_ID_STREAK)
        manager.cancel(NOTIFICATION_ID_PROGRESS)
    }

    private fun canPostNotifications(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return true
        return ContextCompat.checkSelfPermission(
            context,
            Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
    }
}
