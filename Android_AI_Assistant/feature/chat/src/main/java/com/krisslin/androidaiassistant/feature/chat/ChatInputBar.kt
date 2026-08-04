package com.krisslin.androidaiassistant.feature.chat

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.outlined.AttachFile
import androidx.compose.material.icons.outlined.SentimentSatisfied
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val PillShape = RoundedCornerShape(30.dp)

private val PurpleColor = androidx.compose.ui.graphics.Color(0xFFB04CFF)
private val PlaceholderColor = androidx.compose.ui.graphics.Color(0xFF999999)
private val IconTintColor = androidx.compose.ui.graphics.Color(0xFF666666)
private val IconSurfaceColor = androidx.compose.ui.graphics.Color(0xFFF2F3F5)
private val InputTextColor = androidx.compose.ui.graphics.Color(0xFF1C1B18)
private val DisabledSendColor = androidx.compose.ui.graphics.Color(0xFFD9D9D9)

/**
 * Telegram 风格底部悬浮胶囊输入框（Liquid Glass）：
 * - 毛玻璃采样与聊天背景共享同一 ImageBitmap 与裁剪窗口（bgDark/bgBitmap/bgScreenSize 由 ChatRoute 下发），
 *   模糊区域与背景图逐像素同步
 * - 液态玻璃质感：对角玻璃渐变 + 顶部内高光 + 底部内阴影 + 光泽带 + 双层描边
 * - 中间输入区、右侧回形针、最右紫色圆形发送按钮
 * - 聚焦时高光描边变紫，发送按钮按压放大
 */
@Composable
fun ChatInputBar(
    bgDark: Boolean,
    bgBitmap: ImageBitmap,
    bgScreenSize: IntSize,
    input: String,
    onInputChange: (String) -> Unit,
    enabled: Boolean,
    canSend: Boolean,
    canAttach: Boolean,
    onSend: () -> Unit,
    onAttachClick: () -> Unit,
    onEmojiClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    var focused by remember { mutableStateOf(false) }

    val isDark = bgDark
    val outerBorderColor = if (focused) PurpleColor else if (isDark) androidx.compose.ui.graphics.Color.White.copy(alpha = 0.15f) else androidx.compose.ui.graphics.Color.White.copy(alpha = 0.40f)
    val innerBorderColor = if (isDark) androidx.compose.ui.graphics.Color.White.copy(alpha = 0.08f) else androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.08f)
    val inputTextColor = if (isDark) androidx.compose.ui.graphics.Color(0xFFE6E6F0) else InputTextColor
    val placeholderColor = if (isDark) androidx.compose.ui.graphics.Color(0xFF7A7A8C) else PlaceholderColor
    val iconTintColor = if (isDark) androidx.compose.ui.graphics.Color(0xFFA8A8C0) else IconTintColor
    val iconSurfaceColor = if (isDark) androidx.compose.ui.graphics.Color(0xFF2A2A3A) else IconSurfaceColor

    val sendInteraction = remember { MutableInteractionSource() }
    val sendPressed by sendInteraction.collectIsPressedAsState()
    val sendScale by animateFloatAsState(
        targetValue = if (sendPressed) 0.88f else 1f,
        label = "sendScale"
    )

    Box(
        modifier = modifier
            .height(60.dp)
            .graphicsLayer {
                shadowElevation = 10.dp.toPx()
                shape = PillShape
                ambientShadowColor = androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.15f)
                spotShadowColor = androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.25f)
            }
            .clip(PillShape)
    ) {
        LiquidGlassContainer(
            bgDark = bgDark,
            bgBitmap = bgBitmap,
            bgScreenSize = bgScreenSize,
            shape = PillShape,
            modifier = Modifier.matchParentSize()
        ) {

        Box(
            modifier = Modifier
                .matchParentSize()
                .border(
                    width = 1.dp,
                    color = outerBorderColor,
                    shape = PillShape
                )
        )

        Box(
            modifier = Modifier
                .matchParentSize()
                .border(
                    width = 1.dp,
                    color = innerBorderColor,
                    shape = PillShape
                )
        )

        Row(
            modifier = Modifier
                .matchParentSize()
                .padding(horizontal = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(iconSurfaceColor)
                    .clickable(enabled = enabled, onClick = onEmojiClick),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.Outlined.SentimentSatisfied,
                    contentDescription = "表情",
                    tint = iconTintColor
                )
            }

            Spacer(modifier = Modifier.width(6.dp))

            OutlinedTextField(
                value = input,
                onValueChange = onInputChange,
                modifier = Modifier
                    .weight(1f)
                    .onFocusChanged { focused = it.isFocused },
                singleLine = true,
                enabled = enabled,
                textStyle = TextStyle(
                    color = inputTextColor,
                    fontSize = 16.sp
                ),
                placeholder = {
                    Text(
                        text = "输入消息",
                        color = placeholderColor,
                        fontSize = 16.sp
                    )
                },
                colors = OutlinedTextFieldDefaults.colors(
                    focusedContainerColor = androidx.compose.ui.graphics.Color.Transparent,
                    unfocusedContainerColor = androidx.compose.ui.graphics.Color.Transparent,
                    focusedBorderColor = androidx.compose.ui.graphics.Color.Transparent,
                    unfocusedBorderColor = androidx.compose.ui.graphics.Color.Transparent,
                    disabledContainerColor = androidx.compose.ui.graphics.Color.Transparent,
                    disabledBorderColor = androidx.compose.ui.graphics.Color.Transparent,
                    cursorColor = PurpleColor,
                    focusedTextColor = inputTextColor,
                    unfocusedTextColor = inputTextColor
                ),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text)
            )

            Spacer(modifier = Modifier.width(6.dp))

            Box(
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(iconSurfaceColor)
                    .clickable(enabled = canAttach, onClick = onAttachClick),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.Outlined.AttachFile,
                    contentDescription = "附件",
                    tint = iconTintColor
                )
            }

            Spacer(modifier = Modifier.width(8.dp))

            Box(
                modifier = Modifier
                    .size(40.dp)
                    .graphicsLayer {
                        scaleX = sendScale
                        scaleY = sendScale
                    }
                    .clip(CircleShape)
                    .background(
                        if (canSend) PurpleColor
                        else if (isDark) androidx.compose.ui.graphics.Color(0xFF3A3A4A) else DisabledSendColor
                    )
                    .clickable(
                        interactionSource = sendInteraction,
                        indication = null,
                        enabled = canSend,
                        onClick = onSend
                    ),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.Send,
                    contentDescription = "发送",
                    tint = androidx.compose.ui.graphics.Color.White,
                    modifier = Modifier
                        .size(20.dp)
                        .rotate(-45f)
                )
            }
        }
        }
    }
}
