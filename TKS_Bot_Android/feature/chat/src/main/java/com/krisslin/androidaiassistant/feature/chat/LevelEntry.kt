package com.krisslin.androidaiassistant.feature.chat

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

/**
 * 等级/积分入口胶囊（PRD FR-15）。
 *
 * 位置：聊天页顶部左上角，紧贴联系人卡下方（由 [ChatTopBarRow] 的左列承载）。
 * 整体可点击，进入「我的陪伴」个人页。
 *
 * EDGE-7：等级默认态（`NONE`）不设专属文案，断签回落与从未升级的新用户共用同一套默认展示，
 * 这里统一显示中性的“还没有等级称号”。
 */
@Composable
fun LevelEntryChip(
    levelName: String,
    balance: Int,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    Surface(
        onClick = onClick,
        modifier = modifier,
        shape = RoundedCornerShape(percent = 50),
        color = MaterialTheme.colorScheme.surface.copy(alpha = 0.92f),
        contentColor = MaterialTheme.colorScheme.onSurface,
        tonalElevation = 3.dp,
        shadowElevation = 3.dp
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Text(text = "🐼", style = MaterialTheme.typography.labelLarge)
            Text(
                text = levelName.ifBlank { "还没有等级称号" },
                style = MaterialTheme.typography.labelLarge,
                // 既有隐患修复：原先未约束行数，长称号（如以后的词组称号）会换行撑高胶囊。
                // weight(1f, fill = false) 让称号在空间不足时优先让位并省略，而不是挤压积分。
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f, fill = false)
            )
            Text(
                text = "$balance 分",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.primary,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1
            )
        }
    }
}

/**
 * 升级 / 等级恢复庆祝弹窗（PRD FR-16 升级庆祝；EDGE-11 区分“首次达成”与“补签恢复”文案）。
 */
@Composable
fun LevelCelebrationDialog(
    celebration: LevelCelebrationUi,
    onDismiss: () -> Unit
) {
    val isRestore = celebration.changeType == "RESTORE"
    val title = if (isRestore) "等级已恢复" else "🎉 升级啦！"
    val body = buildString {
        if (isRestore) {
            append("补上去的那天没白费，称号「${celebration.levelName}」回来了。\n")
        } else {
            append("陪立希的第 ${celebration.continuousDays} 天，解锁新称号「${celebration.levelName}」。\n")
        }
        append("当前连续陪伴 ${celebration.continuousDays} 天。")
        val nextName = celebration.nextLevelName
        val remaining = celebration.daysToNextLevel
        if (nextName != null && remaining != null) {
            append("\n距「$nextName」还差 $remaining 天。")
        } else if (nextName == null) {
            append("\n已经是最高等级「传奇熊猫」了。")
        }
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(text = title, fontWeight = FontWeight.Bold) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(text = body)
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text(text = if (isRestore) "太好了" else "继续陪他", color = Color.Unspecified)
            }
        }
    )
}
