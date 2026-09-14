package com.krisslin.androidaiassistant.feature.settings

import android.content.Context
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode

class ThemePreferenceStore(context: Context) {
    private val prefs = context.getSharedPreferences("app_theme", Context.MODE_PRIVATE)

    var themeMode by mutableStateOf(readThemeMode())
        private set

    fun updateThemeMode(mode: ThemeMode) {
        themeMode = mode
        prefs.edit().putString(KEY_THEME_MODE, mode.name).apply()
    }

    private fun readThemeMode(): ThemeMode {
        val raw = prefs.getString(KEY_THEME_MODE, ThemeMode.SYSTEM.name) ?: ThemeMode.SYSTEM.name
        return runCatching { ThemeMode.valueOf(raw) }.getOrElse { ThemeMode.SYSTEM }
    }

    companion object {
        private const val KEY_THEME_MODE = "theme_mode"
    }
}
