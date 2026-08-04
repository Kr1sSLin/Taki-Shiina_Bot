package com.krisslin.androidaiassistant.core.ui.theme

import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp

enum class ThemeMode {
    SYSTEM,
    LIGHT,
    DARK
}

private val LightColors = lightColorScheme(
    primary = Color(0xFF7C5CB0),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE6D9F7),
    onPrimaryContainer = Color(0xFF241A38),
    secondary = Color(0xFF8A7BA8),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFE9E2F4),
    onSecondaryContainer = Color(0xFF221A30),
    background = Color(0xFFF3EDFB),
    onBackground = Color(0xFF1F1B27),
    surface = Color(0xFFFBF8FF),
    onSurface = Color(0xFF1F1B27),
    surfaceVariant = Color(0xFFE7DFF1),
    onSurfaceVariant = Color(0xFF4A4457),
    error = Color(0xFFB3261E),
    onError = Color.White
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFA0A0E0),
    onPrimary = Color(0xFF1A1A3A),
    primaryContainer = Color(0xFF2E2E5C),
    onPrimaryContainer = Color(0xFFD8D8F5),
    secondary = Color(0xFF8E8EB8),
    onSecondary = Color(0xFF1A1A2E),
    secondaryContainer = Color(0xFF262640),
    onSecondaryContainer = Color(0xFFD0D0EA),
    background = Color(0xFF0A0A14),
    onBackground = Color(0xFFDCDCE6),
    surface = Color(0xFF12121E),
    onSurface = Color(0xFFDCDCE6),
    surfaceVariant = Color(0xFF26263A),
    onSurfaceVariant = Color(0xFFB4B4CC),
    error = Color(0xFFF2B8B5),
    onError = Color(0xFF601410)
)

@Immutable
data class AppThemeState(
    val themeMode: ThemeMode = ThemeMode.SYSTEM
)

val LocalAppThemeState = staticCompositionLocalOf { AppThemeState() }

/** 主题切换动画锚点上报通道：由切换按钮把自身位置（root 坐标系）上报给 ThemeRevealContainer */
val LocalThemeRevealAnchor = staticCompositionLocalOf<((Offset) -> Unit)?> { null }

/** 主题切换动画状态：progress 0→1，anchor 为扩散圆心（root 坐标系，可能为空） */
@Immutable
data class ThemeRevealState(
    val progress: Float = 1f,
    val anchor: Offset? = null
)

val LocalThemeRevealState = staticCompositionLocalOf { ThemeRevealState() }

/**
 * 在两套 ColorScheme 之间逐字段插值。
 * LightColors 视为 0f、DarkColors 视为 1f。
 */
private fun lerpColorSchemes(from: ColorScheme, to: ColorScheme, t: Float): ColorScheme =
    from.copy(
        primary = lerp(from.primary, to.primary, t),
        onPrimary = lerp(from.onPrimary, to.onPrimary, t),
        primaryContainer = lerp(from.primaryContainer, to.primaryContainer, t),
        onPrimaryContainer = lerp(from.onPrimaryContainer, to.onPrimaryContainer, t),
        inversePrimary = lerp(from.inversePrimary, to.inversePrimary, t),
        secondary = lerp(from.secondary, to.secondary, t),
        onSecondary = lerp(from.onSecondary, to.onSecondary, t),
        secondaryContainer = lerp(from.secondaryContainer, to.secondaryContainer, t),
        onSecondaryContainer = lerp(from.onSecondaryContainer, to.onSecondaryContainer, t),
        tertiary = lerp(from.tertiary, to.tertiary, t),
        onTertiary = lerp(from.onTertiary, to.onTertiary, t),
        tertiaryContainer = lerp(from.tertiaryContainer, to.tertiaryContainer, t),
        onTertiaryContainer = lerp(from.onTertiaryContainer, to.onTertiaryContainer, t),
        background = lerp(from.background, to.background, t),
        onBackground = lerp(from.onBackground, to.onBackground, t),
        surface = lerp(from.surface, to.surface, t),
        onSurface = lerp(from.onSurface, to.onSurface, t),
        surfaceVariant = lerp(from.surfaceVariant, to.surfaceVariant, t),
        onSurfaceVariant = lerp(from.onSurfaceVariant, to.onSurfaceVariant, t),
        surfaceTint = lerp(from.surfaceTint, to.surfaceTint, t),
        inverseSurface = lerp(from.inverseSurface, to.inverseSurface, t),
        inverseOnSurface = lerp(from.inverseOnSurface, to.inverseOnSurface, t),
        error = lerp(from.error, to.error, t),
        onError = lerp(from.onError, to.onError, t),
        errorContainer = lerp(from.errorContainer, to.errorContainer, t),
        onErrorContainer = lerp(from.onErrorContainer, to.onErrorContainer, t),
        outline = lerp(from.outline, to.outline, t),
        outlineVariant = lerp(from.outlineVariant, to.outlineVariant, t),
        scrim = lerp(from.scrim, to.scrim, t)
    )

@Composable
fun AppTheme(
    themeMode: ThemeMode,
    darkThemeDetected: Boolean,
    revealProgress: Float? = null,
    content: @Composable () -> Unit
) {
    val useDark = when (themeMode) {
        ThemeMode.SYSTEM -> darkThemeDetected
        ThemeMode.LIGHT -> false
        ThemeMode.DARK -> true
    }

    val colorScheme = if (revealProgress == null) {
        if (useDark) DarkColors else LightColors
    } else {
        val t = if (useDark) revealProgress else 1f - revealProgress
        lerpColorSchemes(LightColors, DarkColors, t)
    }

    CompositionLocalProvider(
        LocalAppThemeState provides AppThemeState(themeMode)
    ) {
        MaterialTheme(
            colorScheme = colorScheme,
            content = content
        )
    }
}
