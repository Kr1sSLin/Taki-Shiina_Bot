package com.krisslin.androidaiassistant.core.push

import androidx.work.Data
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import java.time.Duration
import java.time.LocalDateTime
import java.time.LocalTime
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class ReminderScheduler @Inject constructor(
    private val workManager: WorkManager
) {
    fun schedule(requestId: String, target: String, text: String) {
        val now = LocalDateTime.now()
        val targetTime = runCatching { LocalTime.parse(target) }.getOrNull() ?: return
        var next = now.withHour(targetTime.hour).withMinute(targetTime.minute).withSecond(0).withNano(0)
        if (!next.isAfter(now)) {
            next = next.plusDays(1)
        }
        val delay = Duration.between(now, next)

        val data = Data.Builder()
            .putString(ReminderWorker.KEY_TARGET, target)
            .putString(ReminderWorker.KEY_TEXT, text)
            .build()

        val work = OneTimeWorkRequestBuilder<ReminderWorker>()
            .setInputData(data)
            .setInitialDelay(delay)
            .build()

        workManager.enqueueUniqueWork(
            "reminder_${requestId}_${target}_${text.hashCode()}",
            ExistingWorkPolicy.KEEP,
            work
        )
    }
}
