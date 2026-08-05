package com.krisslin.androidaiassistant.core.push

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters

class ReminderWorker(
    appContext: Context,
    workerParams: WorkerParameters
) : CoroutineWorker(appContext, workerParams) {

    override suspend fun doWork(): Result {
        val target = inputData.getString(KEY_TARGET).orEmpty()
        val text = inputData.getString(KEY_TEXT).orEmpty()
        // 统一走 AppNotifier：固定 ID 替换展示 + 30 分钟超时 + 支持 App 前台统一清除
        AppNotifier(applicationContext).showReminder(text, target)
        return Result.success()
    }

    companion object {
        const val KEY_TARGET = "target"
        const val KEY_TEXT = "text"
    }
}
