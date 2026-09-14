package com.krisslin.androidaiassistant.feature.history

import android.text.format.DateUtils
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.outlined.NotificationsNone
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 通知中心（原「错误中心」）。
 *
 * 两类记录：
 * - **关于我的记录**：Bot 侧抽取的用户客观事实（`UserFactEntity`）
 * - **Bot 错误通知**：服务端下发的错误码 + 说明（`BotNotificationEntity`），未读以左侧圆点标识
 *
 * 本次为「重构」而非「裸接入口」，原因（均可在改动前的源码里核对）：
 * 1. 原实现只有 `Column(padding(16dp))` —— 没有 Scaffold、没有标题栏、没有返回键、
 *    不处理状态栏内边距，正文直接顶到状态栏下方；
 * 2. 卡片用默认 `Card()`，容器色取 M3 基线 `surfaceContainerLow`，
 *    而 `AppTheme` 未覆盖该槽位，导致卡片色与项目紫调 `surface` 不是一套色系；
 * 3. 「用户客观事实」是后端画像术语，直接暴露给终端用户语义突兀，改为「关于我的记录」；
 * 4. 缺少空态：新装用户打开是一片空白，分不清是加载中还是真的没有。
 *
 * 命名债：所在模块仍名 `feature:history`、文件仍名 `HistoryEntry.kt`，与「通知中心」不一致。
 * 模块改名会牵动 settings.gradle / 目录结构，属独立改动，本次未一并处理。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotificationCenterRoute(
    viewModel: HistoryViewModel = hiltViewModel(),
    onBack: () -> Unit = {}
) {
    val state by viewModel.uiState.collectAsState()
    val isEmpty = state.userFacts.isEmpty() && state.notifications.isEmpty()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("通知中心") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "返回"
                        )
                    }
                },
                actions = {
                    // 没有未读时不显示「全部已读」，避免无效操作项
                    if (state.unreadCount > 0) {
                        TextButton(onClick = viewModel::markAllAsRead) {
                            Text("全部已读")
                        }
                    }
                }
            )
        }
    ) { padding ->
        if (isEmpty) {
            EmptyState(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding)
            )
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding)
                    .padding(horizontal = 16.dp),
                contentPadding = PaddingValues(vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                if (state.userFacts.isNotEmpty()) {
                    item(key = "facts_title") {
                        SectionTitle("关于我的记录")
                    }
                    items(state.userFacts, key = { it.factId }) { item ->
                        RecordCard {
                            Text(
                                text = item.fact,
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Spacer(modifier = Modifier.height(4.dp))
                            Text(
                                text = formatTimestamp(item.timestamp),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }

                if (state.notifications.isNotEmpty()) {
                    item(key = "notifications_title") {
                        SectionTitle("Bot 错误通知")
                    }
                    items(state.notifications, key = { it.notificationId }) { item ->
                        val unread = item.isRead == 0
                        RecordCard(onClick = { viewModel.markAsRead(item.notificationId) }) {
                            Row(verticalAlignment = Alignment.Top) {
                                // 未读圆点：已读时保持透明占位，文字左边缘不会跳动
                                Box(
                                    modifier = Modifier
                                        .padding(top = 6.dp)
                                        .size(8.dp)
                                        .clip(CircleShape)
                                        .background(
                                            if (unread) MaterialTheme.colorScheme.primary
                                            else Color.Transparent
                                        )
                                )
                                Spacer(modifier = Modifier.width(10.dp))
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = item.errorCode,
                                        style = MaterialTheme.typography.titleSmall,
                                        color = if (unread) {
                                            MaterialTheme.colorScheme.onSurface
                                        } else {
                                            MaterialTheme.colorScheme.onSurfaceVariant
                                        },
                                        fontWeight = if (unread) FontWeight.SemiBold else FontWeight.Normal,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis
                                    )
                                    Spacer(modifier = Modifier.height(2.dp))
                                    Text(
                                        text = item.message,
                                        style = MaterialTheme.typography.bodyMedium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                    Spacer(modifier = Modifier.height(4.dp))
                                    Text(
                                        text = formatTimestamp(item.timestamp),
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(top = 8.dp)
    )
}

/**
 * 统一卡片容器：`surface` 容器色 + 12dp 圆角 + 12dp 内边距。
 * 显式指定容器色是本次重构的关键 —— 让卡片与设置页同色系，不再落到 M3 基线槽位。
 */
@Composable
private fun RecordCard(
    onClick: (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    ) {
        Column(modifier = Modifier.padding(12.dp), content = content)
    }
}

@Composable
private fun EmptyState(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(horizontal = 32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Icon(
            imageVector = Icons.Outlined.NotificationsNone,
            contentDescription = null,
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.size(48.dp)
        )
        Spacer(modifier = Modifier.height(12.dp))
        Text(
            text = "暂无错误记录",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

private fun formatTimestamp(timestamp: Long): String {
    val pattern = if (DateUtils.isToday(timestamp)) "HH:mm" else "MM-dd HH:mm"
    return SimpleDateFormat(pattern, Locale.getDefault()).format(Date(timestamp))
}
