package com.krisslin.androidaiassistant.core.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.graphics.Color

enum class ThemeMode {
    SYSTEM,
    LIGHT,
    DARK
}

private val LightColors = lightColorScheme(
    primary = Color(0xFF3A5A40),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFCDE6D1),
    onPrimaryContainer = Color(0xFF0F1F13),
    secondary = Color(0xFF4C5C68),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFDDE3E8),
    onSecondaryContainer = Color(0xFF11181C),
    background = Color(0xFFF8F7F2),
    onBackground = Color(0xFF1C1B18),
    surface = Color(0xFFFEFBF4),
    onSurface = Color(0xFF1C1B18),
    error = Color(0xFFB3261E),
    onError = Color.White
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFB7E4C7),
    onPrimary = Color(0xFF102014),
    primaryContainer = Color(0xFF214F31),
    onPrimaryContainer = Color(0xFFD8F5E2),
    secondary = Color(0xFFA9B6C1),
    onSecondary = Color(0xFF182027),
    secondaryContainer = Color(0xFF33404A),
    onSecondaryContainer = Color(0xFFDCE5EB),
    background = Color(0xFF111411),
    onBackground = Color(0xFFE2E3DD),
    surface = Color(0xFF171A17),
    onSurface = Color(0xFFE2E3DD),
    error = Color(0xFFF2B8B5),
    onError = Color(0xFF601410)
)

@Immutable
data class AppThemeState(
    val themeMode: ThemeMode = ThemeMode.SYSTEM
)

val LocalAppThemeState = staticCompositionLocalOf { AppThemeState() }

@Composable
fun AppTheme(
    themeMode: ThemeMode,
    darkThemeDetected: Boolean,
    content: @Composable () -> Unit
) {
    val useDark = when (themeMode) {
        ThemeMode.SYSTEM -> darkThemeDetected
        ThemeMode.LIGHT -> false
        ThemeMode.DARK -> true
    }

    CompositionLocalProvider(
        LocalAppThemeState provides AppThemeState(themeMode)
    ) {
        MaterialTheme(
            colorScheme = if (useDark) DarkColors else LightColors,
            content = content
        )
    }
}
