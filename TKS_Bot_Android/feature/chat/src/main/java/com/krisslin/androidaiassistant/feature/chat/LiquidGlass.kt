package com.krisslin.androidaiassistant.feature.chat

import android.graphics.RenderEffect
import android.graphics.Shader
import android.os.Build
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import kotlin.math.max
import kotlin.math.roundToInt

/** 与 Image(ContentScale.Crop) 完全一致的背景图居中裁剪窗口 */
internal fun backgroundCropRect(
    imgWidth: Int,
    imgHeight: Int,
    canvasWidth: Int,
    canvasHeight: Int
): Rect {
    val scale = max(
        canvasWidth.toFloat() / imgWidth,
        canvasHeight.toFloat() / imgHeight
    )
    val srcW = canvasWidth / scale
    val srcH = canvasHeight / scale
    val srcX = (imgWidth - srcW) / 2f
    val srcY = (imgHeight - srcH) / 2f
    return Rect(srcX, srcY, srcX + srcW, srcY + srcH)
}

/**
 * Liquid Glass 容器：与聊天背景同步的液态玻璃面板。
 *
 * 分层渲染（与 ChatInputBar 完全同一套效果）：
 * - 模糊层：RenderEffect 模糊与聊天背景逐像素对齐的背景图（Android 12+，低版本透明）
 * - 玻璃层：对角渐变 + 光泽带 + 顶部内高光 + 底部内阴影，随日/夜间模式切换深浅色系
 * - 内容由调用方提供（描边、内部控件等）
 */
@Composable
fun LiquidGlassContainer(
    bgDark: Boolean,
    bgBitmap: ImageBitmap,
    bgScreenSize: IntSize,
    shape: Shape,
    modifier: Modifier = Modifier,
    content: @Composable androidx.compose.foundation.layout.BoxScope.() -> Unit
) {
    var containerOrigin by remember { mutableStateOf(Offset.Zero) }
    val isDark = bgDark
    val glassColors = if (isDark) {
        listOf(
            Color(0xFF16161F).copy(alpha = 0.88f),
            Color(0xFF0C0C12).copy(alpha = 0.55f)
        )
    } else {
        listOf(
            Color.White.copy(alpha = 0.92f),
            Color.White.copy(alpha = 0.55f)
        )
    }
    val glossColor = if (isDark) Color.White.copy(alpha = 0.10f) else Color.White.copy(alpha = 0.28f)
    val topGlowColor = if (isDark) Color.White.copy(alpha = 0.10f) else Color.White.copy(alpha = 0.60f)
    val bottomShadowColor = if (isDark) Color.Black.copy(alpha = 0.25f) else Color.Black.copy(alpha = 0.10f)

    val blurEffect = remember {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            RenderEffect.createBlurEffect(28f, 28f, Shader.TileMode.DECAL).asComposeRenderEffect()
        } else {
            null
        }
    }

    Box(
        modifier = modifier
            .onGloballyPositioned { containerOrigin = it.boundsInWindow().topLeft }
    ) {
        Box(
            modifier = Modifier
                .matchParentSize()
                .graphicsLayer { renderEffect = blurEffect }
                .clip(shape)
                .drawWithContent {
                    if (blurEffect != null) {
                        val crop = backgroundCropRect(
                            imgWidth = bgBitmap.width,
                            imgHeight = bgBitmap.height,
                            canvasWidth = bgScreenSize.width,
                            canvasHeight = bgScreenSize.height
                        )
                        if (crop.width > 0f && crop.height > 0f) {
                            val scale = crop.width / bgScreenSize.width
                            val srcOffset = IntOffset(
                                x = (crop.left + containerOrigin.x / scale).roundToInt(),
                                y = (crop.top + containerOrigin.y / scale).roundToInt()
                            )
                            val srcSize = IntSize(
                                width = (size.width / scale).roundToInt(),
                                height = (size.height / scale).roundToInt()
                            )
                            drawImage(
                                image = bgBitmap,
                                srcOffset = srcOffset,
                                srcSize = srcSize,
                                dstOffset = IntOffset.Zero,
                                dstSize = IntSize(
                                    width = size.width.roundToInt(),
                                    height = size.height.roundToInt()
                                )
                            )
                        }
                    }
                }
        )

        Box(
            modifier = Modifier
                .matchParentSize()
                .clip(shape)
                .drawBehind {
                    drawRect(
                        brush = Brush.linearGradient(
                            colors = glassColors,
                            start = Offset.Zero,
                            end = Offset(size.width, size.height)
                        )
                    )
                    drawRect(
                        brush = Brush.linearGradient(
                            colors = listOf(
                                glossColor,
                                Color.Transparent,
                                Color.Transparent
                            ),
                            start = Offset(size.width * 0.15f, size.height * 0.05f),
                            end = Offset(size.width * 0.75f, size.height)
                        )
                    )
                    drawRect(
                        brush = Brush.verticalGradient(
                            colors = listOf(
                                topGlowColor,
                                Color.Transparent
                            )
                        ),
                        topLeft = Offset(0f, 0f),
                        size = Size(size.width, 3.dp.toPx())
                    )
                    drawRect(
                        brush = Brush.verticalGradient(
                            colors = listOf(
                                Color.Transparent,
                                bottomShadowColor
                            )
                        ),
                        topLeft = Offset(0f, size.height - 4.dp.toPx()),
                        size = Size(size.width, 4.dp.toPx())
                    )
                }
        )

        content()
    }
}
