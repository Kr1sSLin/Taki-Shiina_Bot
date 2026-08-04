package com.krisslin.androidaiassistant.feature.chat

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DarkMode
import androidx.compose.material.icons.filled.LightMode
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.krisslin.androidaiassistant.core.ui.theme.LocalThemeRevealAnchor
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.sin

private val TopCardShape = RoundedCornerShape(28.dp)

/**
 * 页面顶部悬浮卡片行：居中缩小的联系人卡（图片头像）+ 右侧缩小的操作按钮卡。
 * 两张卡片均为 Liquid Glass 毛玻璃 + 紫色叠加 + 白描边 + 柔和阴影。
 */
@Composable
fun ChatTopBarRow(
    bgDark: Boolean,
    bgBitmap: ImageBitmap,
    bgScreenSize: IntSize,
    isDarkMode: Boolean,
    contactName: String,
    statusText: String,
    onToggleTheme: () -> Unit,
    onNavigateToSettings: () -> Unit,
    onReconnect: () -> Unit,
    modifier: Modifier = Modifier
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Spacer(modifier = Modifier.weight(1f))
        ContactCard(
            bgDark = bgDark,
            bgBitmap = bgBitmap,
            bgScreenSize = bgScreenSize,
            contactName = contactName,
            statusText = statusText,
            modifier = Modifier.width(200.dp)
        )
        Spacer(modifier = Modifier.weight(1f))
        ActionsCard(
            bgDark = bgDark,
            bgBitmap = bgBitmap,
            bgScreenSize = bgScreenSize,
            isDarkMode = isDarkMode,
            onToggleTheme = onToggleTheme,
            onNavigateToSettings = onNavigateToSettings,
            onReconnect = onReconnect,
            modifier = Modifier.width(140.dp)
        )
    }
}

@Composable
private fun ContactCard(
    bgDark: Boolean,
    bgBitmap: ImageBitmap,
    bgScreenSize: IntSize,
    contactName: String,
    statusText: String,
    modifier: Modifier = Modifier
) {
    LiquidGlassContainer(
        bgDark = bgDark,
        bgBitmap = bgBitmap,
        bgScreenSize = bgScreenSize,
        shape = TopCardShape,
        modifier = modifier.height(56.dp)
    ) {
        Box(
            modifier = Modifier
                .matchParentSize()
                .clip(TopCardShape)
                .background(Color(0xFFB04CFF).copy(alpha = 0.08f))
        )
        Box(
            modifier = Modifier
                .matchParentSize()
                .border(width = 1.dp, color = Color.White.copy(alpha = 0.5f), shape = TopCardShape)
        )
        Row(
            modifier = Modifier
                .matchParentSize()
                .padding(horizontal = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(CircleShape)
                    .border(width = 2.dp, color = Color.White.copy(alpha = 0.85f), shape = CircleShape)
            ) {
                Image(
                    painter = painterResource(R.drawable.avatar),
                    contentDescription = "联系人头像",
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Crop
                )
                Box(
                    modifier = Modifier
                        .matchParentSize()
                        .drawBehind {
                            drawRect(
                                brush = Brush.linearGradient(
                                    colors = listOf(
                                        Color.White.copy(alpha = 0.35f),
                                        Color.Transparent,
                                        Color.White.copy(alpha = 0.15f)
                                    ),
                                    start = Offset(0f, 0f),
                                    end = Offset(size.width, size.height)
                                )
                            )
                            val ringStroke = Stroke(width = 2.dp.toPx(), cap = StrokeCap.Round)
                            drawArc(
                                color = Color.White.copy(alpha = 0.55f),
                                startAngle = 180f,
                                sweepAngle = 180f,
                                useCenter = false,
                                topLeft = Offset(0f, 0f),
                                size = size,
                                style = ringStroke
                            )
                            drawArc(
                                color = Color.Black.copy(alpha = 0.18f),
                                startAngle = 0f,
                                sweepAngle = 180f,
                                useCenter = false,
                                topLeft = Offset(0f, 0f),
                                size = size,
                                style = ringStroke
                            )
                        }
                )
            }

            Spacer(modifier = Modifier.width(10.dp))

            Column {
                Text(
                    text = contactName,
                    color = if (bgDark) Color(0xFFE6E6F0) else Color(0xFF1C1B18),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(modifier = Modifier.height(2.dp))
                Text(
                    text = statusText,
                    color = if (bgDark) Color(0xFF8A8A9C) else Color(0xFF999999),
                    fontSize = 11.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
    }
}

@Composable
private fun ActionsCard(
    bgDark: Boolean,
    bgBitmap: ImageBitmap,
    bgScreenSize: IntSize,
    isDarkMode: Boolean,
    onToggleTheme: () -> Unit,
    onNavigateToSettings: () -> Unit,
    onReconnect: () -> Unit,
    modifier: Modifier = Modifier
) {
    val reportAnchor = LocalThemeRevealAnchor.current
    val iconColor = if (bgDark) Color(0xFFE6E6F0) else Color(0xFF1C1B18)

    LiquidGlassContainer(
        bgDark = bgDark,
        bgBitmap = bgBitmap,
        bgScreenSize = bgScreenSize,
        shape = TopCardShape,
        modifier = modifier.height(56.dp)
    ) {
        Box(
            modifier = Modifier
                .matchParentSize()
                .clip(TopCardShape)
                .background(Color(0xFFB04CFF).copy(alpha = 0.08f))
        )
        Box(
            modifier = Modifier
                .matchParentSize()
                .border(width = 1.dp, color = Color.White.copy(alpha = 0.5f), shape = TopCardShape)
        )
        Row(
            modifier = Modifier
                .matchParentSize()
                .padding(horizontal = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(2.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .clickable(onClick = onToggleTheme)
                    .onGloballyPositioned { coords ->
                        reportAnchor?.invoke(coords.boundsInRoot().center)
                    },
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = if (isDarkMode) Icons.Filled.LightMode else Icons.Filled.DarkMode,
                    contentDescription = if (isDarkMode) "切换日间模式" else "切换夜间模式",
                    tint = iconColor,
                    modifier = Modifier.size(20.dp)
                )
            }
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .clickable(onClick = onReconnect),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.Filled.Refresh,
                    contentDescription = "重新连接",
                    tint = iconColor,
                    modifier = Modifier.size(20.dp)
                )
            }
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .clickable(onClick = onNavigateToSettings),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.Filled.Settings,
                    contentDescription = "设置",
                    tint = iconColor,
                    modifier = Modifier.size(20.dp)
                )
            }
        }
    }
}

/**
 * 顶部背景层：紫粉色渐变 + Telegram 风格线稿涂鸦（低透明度），替代原顶部黑色 scrim。
 */
@Composable
fun TopDoodleBackground(
    isDarkMode: Boolean,
    modifier: Modifier = Modifier
) {
    val gradientColors = if (isDarkMode) {
        listOf(
            Color(0xFF7C3AED).copy(alpha = 0.30f),
            Color(0xFFE85D9A).copy(alpha = 0.28f),
            Color.Transparent
        )
    } else {
        listOf(
            Color(0xFFB04CFF).copy(alpha = 0.50f),
            Color(0xFFFF7EB6).copy(alpha = 0.45f),
            Color.Transparent
        )
    }
    val doodleColor = Color.White.copy(alpha = if (isDarkMode) 0.10f else 0.16f)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(320.dp)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Brush.verticalGradient(colors = gradientColors))
        )
        Canvas(modifier = Modifier.fillMaxSize()) {
            val stroke = 1.5.dp.toPx()
            doodleStar(Offset(36.dp.toPx(), 64.dp.toPx()), outer = 16.dp.toPx(), inner = 7.dp.toPx(), doodleColor, stroke)
            doodleCloud(Offset(104.dp.toPx(), 46.dp.toPx()), r = 18.dp.toPx(), doodleColor, stroke)
            doodleLightning(Offset(192.dp.toPx(), 88.dp.toPx()), h = 26.dp.toPx(), w = 18.dp.toPx(), doodleColor, stroke)
            doodleHeart(Offset(266.dp.toPx(), 52.dp.toPx()), r = 12.dp.toPx(), doodleColor, stroke)
            doodleCircle(Offset(330.dp.toPx(), 102.dp.toPx()), r = 14.dp.toPx(), doodleColor, stroke)
            doodleMoon(Offset(398.dp.toPx(), 48.dp.toPx()), r = 16.dp.toPx(), doodleColor, stroke)
            doodleSmile(Offset(468.dp.toPx(), 92.dp.toPx()), r = 15.dp.toPx(), doodleColor, stroke)
            doodleFlower(Offset(534.dp.toPx(), 56.dp.toPx()), r = 10.dp.toPx(), doodleColor, stroke)
        }
    }
}

private fun DrawScope.doodleStar(center: Offset, outer: Float, inner: Float, color: Color, stroke: Float) {
    val path = Path()
    for (i in 0 until 10) {
        val r = if (i % 2 == 0) outer else inner
        val angle = -PI / 2 + i * PI / 5
        val x = center.x + r * cos(angle).toFloat()
        val y = center.y + r * sin(angle).toFloat()
        if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
    }
    path.close()
    drawPath(path, color, style = Stroke(width = stroke))
}

private fun DrawScope.doodleCloud(center: Offset, r: Float, color: Color, stroke: Float) {
    drawCircle(color, radius = r, center = center, style = Stroke(width = stroke))
    drawCircle(color, radius = r * 0.7f, center = center + Offset(-r * 1.15f, r * 0.35f), style = Stroke(width = stroke))
    drawCircle(color, radius = r * 0.75f, center = center + Offset(r * 1.15f, r * 0.3f), style = Stroke(width = stroke))
    val bottom = center.y + r * 0.9f
    drawLine(color, Offset(center.x - r * 1.85f, bottom), Offset(center.x + r * 1.9f, bottom), strokeWidth = stroke)
}

private fun DrawScope.doodleLightning(center: Offset, h: Float, w: Float, color: Color, stroke: Float) {
    val path = Path().apply {
        moveTo(center.x, center.y - h)
        lineTo(center.x + w * 0.25f, center.y + h * 0.3f)
        lineTo(center.x - w * 0.15f, center.y + h * 0.3f)
        lineTo(center.x + w * 0.15f, center.y + h)
        lineTo(center.x + w * 0.5f, center.y - h * 0.1f)
        lineTo(center.x + w * 0.05f, center.y - h * 0.1f)
        lineTo(center.x + w * 0.5f, center.y - h)
        close()
    }
    drawPath(path, color, style = Stroke(width = stroke))
}

private fun DrawScope.doodleHeart(center: Offset, r: Float, color: Color, stroke: Float) {
    val path = Path().apply {
        moveTo(center.x, center.y + r * 0.9f)
        cubicTo(
            center.x - r * 1.6f, center.y - r * 0.4f,
            center.x - r * 0.8f, center.y - r * 1.5f,
            center.x, center.y - r * 0.6f
        )
        cubicTo(
            center.x + r * 0.8f, center.y - r * 1.5f,
            center.x + r * 1.6f, center.y - r * 0.4f,
            center.x, center.y + r * 0.9f
        )
        close()
    }
    drawPath(path, color, style = Stroke(width = stroke))
}

private fun DrawScope.doodleCircle(center: Offset, r: Float, color: Color, stroke: Float) {
    drawCircle(color, radius = r, center = center, style = Stroke(width = stroke))
}

private fun DrawScope.doodleMoon(center: Offset, r: Float, color: Color, stroke: Float) {
    drawArc(
        color = color,
        startAngle = -40f,
        sweepAngle = 220f,
        useCenter = false,
        topLeft = center - Offset(r, r),
        size = Size(r * 2f, r * 2f),
        style = Stroke(width = stroke, cap = StrokeCap.Round)
    )
}

private fun DrawScope.doodleSmile(center: Offset, r: Float, color: Color, stroke: Float) {
    drawCircle(color, radius = r, center = center, style = Stroke(width = stroke))
    drawCircle(color, radius = 1.6.dp.toPx(), center = center + Offset(-r * 0.35f, -r * 0.25f), style = Stroke(width = stroke))
    drawCircle(color, radius = 1.6.dp.toPx(), center = center + Offset(r * 0.35f, -r * 0.25f), style = Stroke(width = stroke))
    drawArc(
        color = color,
        startAngle = 20f,
        sweepAngle = 140f,
        useCenter = false,
        topLeft = center - Offset(r * 0.45f, -r * 0.1f),
        size = Size(r * 0.9f, r * 0.7f),
        style = Stroke(width = stroke, cap = StrokeCap.Round)
    )
}

private fun DrawScope.doodleFlower(center: Offset, r: Float, color: Color, stroke: Float) {
    repeat(6) { i ->
        val angle = i * PI / 3
        val cx = center.x + r * 0.9f * cos(angle).toFloat()
        val cy = center.y + r * 0.9f * sin(angle).toFloat()
        drawCircle(color, radius = r * 0.45f, center = Offset(cx, cy), style = Stroke(width = stroke))
    }
    drawCircle(color, radius = r * 0.4f, center = center, style = Stroke(width = stroke))
}
