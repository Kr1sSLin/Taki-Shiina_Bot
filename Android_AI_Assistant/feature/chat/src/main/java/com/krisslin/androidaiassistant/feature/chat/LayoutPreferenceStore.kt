package com.krisslin.androidaiassistant.feature.chat

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * 聊天页可拖拽元素的位置偏好。
 *
 * **关键设计：存比例（0~1）而非绝对像素。**
 *
 * 绝对像素在以下场景全部失效：键盘弹出压缩可视高度、表情面板展开顶起输入条、
 * 横竖屏切换、不同分辨率机型。存比例后按当前容器尺寸还原，位置天然贴合容器边缘，
 * 也让「夹取到安全区」变成结构性保证而不是补偿逻辑。
 *
 * 落点语义：
 * - `fabFractionX = 0f` → 左边缘；`1f` → 右边缘（松手时吸附到最近一侧，故实际只取 0 / 1）
 * - `fabFractionY = 0f` → 顶栏下方上限；`1f` → 输入条上方下限
 *
 * 登出时**不**还原：位置是设备级偏好（本 App 认证为 JWT + deviceId 白名单，布局与账号无关）。
 * 如需改为登出还原，在 `AppRoot` 的 `LaunchedEffect(tokenState)` 分支里调用 [resetFab]。
 */
class LayoutPreferenceStore(context: Context) {

    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    /** 「+」按钮横向位置比例（0 = 左边缘，1 = 右边缘）。 */
    var fabFractionX by mutableStateOf(prefs.getFloat(KEY_FAB_FX, DEFAULT_FAB_FX))
        private set

    /** 「+」按钮纵向位置比例（0 = 顶栏下方上限，1 = 输入条上方下限）。 */
    var fabFractionY by mutableStateOf(prefs.getFloat(KEY_FAB_FY, DEFAULT_FAB_FY))
        private set

    /** 「长按可拖动」引导动画是否已播放过（只播一次）。 */
    var fabHintShown by mutableStateOf(prefs.getBoolean(KEY_FAB_HINT, false))
        private set

    fun saveFabFraction(x: Float, y: Float) {
        fabFractionX = x.coerceIn(0f, 1f)
        fabFractionY = y.coerceIn(0f, 1f)
        prefs.edit()
            .putFloat(KEY_FAB_FX, fabFractionX)
            .putFloat(KEY_FAB_FY, fabFractionY)
            .apply()
    }

    fun markFabHintShown() {
        fabHintShown = true
        prefs.edit().putBoolean(KEY_FAB_HINT, true).apply()
    }

    fun resetFab() {
        saveFabFraction(DEFAULT_FAB_FX, DEFAULT_FAB_FY)
    }

    private companion object {
        const val PREFS_NAME = "app_chat_layout"
        const val KEY_FAB_FX = "fab_fraction_x"
        const val KEY_FAB_FY = "fab_fraction_y"
        const val KEY_FAB_HINT = "fab_hint_shown"

        /** 默认右下角（与改动前的固定位置一致）。 */
        const val DEFAULT_FAB_FX = 1f
        const val DEFAULT_FAB_FY = 1f
    }
}
