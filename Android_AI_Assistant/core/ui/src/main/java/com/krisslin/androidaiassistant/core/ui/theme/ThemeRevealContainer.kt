package com.krisslin.androidaiassistant.core.ui.theme

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset

/**
 * 主题切换圆形揭示动画容器（单树方案）。
 *
 * 全程只组合一棵 UI 树，无重建、无列表状态重置：
 * - 主题色通过 [AppTheme] 的 revealProgress 在两套 ColorScheme 之间逐字段插值，全局平滑渐变
 * - 切换进度与锚点通过 [LocalThemeRevealState] 下发，供背景图等元素做圆形扩散裁剪
 *
 * 消除闪屏的关键：`remember(targetDark)` 使切换当帧 progress 同步归零，
 * 中间帧显示的就是动画第一帧（旧主题），不存在"完整新主题闪现"的过渡帧。
 */
@Composable
fun ThemeRevealContainer(
    themeMode: ThemeMode,
    targetDark: Boolean,
    content: @Composable (dark: Boolean) -> Unit
) {
    var lastDark by remember { mutableStateOf(targetDark) }
    var hasAnimated by remember { mutableStateOf(false) }
    var revealAnchor by remember { mutableStateOf<Offset?>(null) }
    val revealProgress = remember(targetDark) { Animatable(0f) }

    val reportAnchor: (Offset) -> Unit = { revealAnchor = it }

    LaunchedEffect(targetDark) {
        if (targetDark != lastDark) {
            lastDark = targetDark
            hasAnimated = true
            revealProgress.animateTo(
                targetValue = 1f,
                animationSpec = tween(
                    durationMillis = 650,
                    easing = FastOutSlowInEasing
                )
            )
        } else if (!hasAnimated) {
            hasAnimated = true
            revealProgress.snapTo(1f)
        }
    }

    val progress = revealProgress.value
    val animating = hasAnimated && progress < 1f

    CompositionLocalProvider(
        LocalThemeRevealAnchor provides reportAnchor,
        LocalThemeRevealState provides ThemeRevealState(
            progress = progress,
            anchor = revealAnchor
        )
    ) {
        Box(
            modifier = Modifier.fillMaxSize()
        ) {
            AppTheme(
                themeMode = themeMode,
                darkThemeDetected = lastDark,
                revealProgress = if (animating) progress else null
            ) {
                content(targetDark)
            }
        }
    }
}
