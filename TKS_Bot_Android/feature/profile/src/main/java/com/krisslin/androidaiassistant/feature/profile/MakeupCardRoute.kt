package com.krisslin.androidaiassistant.feature.profile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel

/**
 * 补签卡页（PRD FR-19 ~ FR-22）。
 *
 * 补签**必须由用户点击 + 二次确认**才会调用接口（FR-20），系统不做任何自动使用。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MakeupCardRoute(
    onBack: () -> Unit,
    viewModel: MakeupCardViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()
    val snackbarHostState = remember { SnackbarHostState() }
    // 待确认的补签日期：非空时弹出二次确认
    var pendingDate by remember { mutableStateOf<String?>(null) }

    val message = state.message
    LaunchedEffect(message) {
        if (!message.isNullOrBlank()) {
            snackbarHostState.showSnackbar(message)
            viewModel.consumeMessage()
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("补签卡") },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "返回"
                        )
                    }
                },
                actions = {
                    IconButton(onClick = viewModel::refresh) {
                        Icon(
                            imageVector = Icons.Filled.Refresh,
                            contentDescription = "刷新"
                        )
                    }
                }
            )
        },
        snackbarHost = { SnackbarHost(hostState = snackbarHostState) }
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            when {
                state.loading && state.candidates.isEmpty() && state.history.isEmpty() ->
                    CircularProgressIndicator(modifier = Modifier.align(Alignment.Center))

                state.error != null && state.candidates.isEmpty() && state.history.isEmpty() ->
                    Column(
                        modifier = Modifier
                            .align(Alignment.Center)
                            .padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text(
                            text = state.error.orEmpty(),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.error
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        TextButton(onClick = viewModel::refresh) {
                            Text("重试")
                        }
                    }

                else -> MakeupCardContent(
                    state = state,
                    onRequestUse = { date -> pendingDate = date },
                    onRetry = viewModel::refresh
                )
            }
        }
    }

    val confirmTarget = pendingDate
    if (confirmTarget != null) {
        AlertDialog(
            onDismissRequest = { pendingDate = null },
            title = { Text("使用补签卡") },
            text = {
                Text("确认为 $confirmTarget 补签吗？将消耗 1 张补签卡（当前可用 ${state.available} 张）。")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        pendingDate = null
                        // FR-20：仅在此用户确认回调里发起补签请求
                        viewModel.useCard(confirmTarget)
                    }
                ) {
                    Text("确认补签")
                }
            },
            dismissButton = {
                TextButton(onClick = { pendingDate = null }) {
                    Text("取消")
                }
            }
        )
    }
}

@Composable
private fun MakeupCardContent(
    state: MakeupCardUiState,
    onRequestUse: (String) -> Unit,
    onRetry: () -> Unit
) {
    val error = state.error

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        if (error != null) {
            item(key = "error") {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = error,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.weight(1f)
                    )
                    TextButton(onClick = onRetry) {
                        Text("重试")
                    }
                }
            }
        }

        item(key = "summary") {
            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(12.dp),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
            ) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp)
                ) {
                    Text(
                        text = "可用 ${state.available}/${state.maxAvailable} 张",
                        style = MaterialTheme.typography.titleLarge,
                        color = MaterialTheme.colorScheme.primary
                    )
                    Spacer(modifier = Modifier.height(6.dp))
                    Text(
                        text = if (state.currentMonthGranted) {
                            "本月已发放 ${state.monthlyGrant} 张"
                        } else {
                            "本月尚未发放，发放后可用张数会自动增加"
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurface
                    )
                    Spacer(modifier = Modifier.height(2.dp))
                    Text(
                        text = "累计获得 ${state.totalGranted} 张 · 已使用 ${state.used} 张",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    if (state.atLimit) {
                        Spacer(modifier = Modifier.height(2.dp))
                        Text(
                            text = "已达累积上限，用掉一张后恢复发放",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                }
            }
        }

        item(key = "rule") {
            Text(
                text = "每月 1 日发放 1 张，未使用可跨月结转，最多累积 12 张；需手动使用，可为任意历史缺口日期补签",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        item(key = "candidates_title") {
            Text(
                text = "可补签日期",
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
        }

        if (state.candidates.isEmpty()) {
            item(key = "candidates_empty") {
                Text(
                    text = "暂无需要补签的日期",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            items(state.candidates, key = { it.date }) { candidate ->
                CandidateRow(
                    candidate = candidate,
                    enabled = state.available > 0 && !state.using,
                    onUse = { onRequestUse(candidate.date) }
                )
            }
        }

        item(key = "history_title") {
            Text(
                text = "补签卡记录",
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
        }

        if (state.history.isEmpty()) {
            item(key = "history_empty") {
                Text(
                    text = "还没有补签卡记录",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            items(state.history, key = { it.id }) { card ->
                HistoryRow(card = card)
            }
        }
    }
}

@Composable
private fun CandidateRow(
    candidate: MakeupCardCandidateUi,
    enabled: Boolean,
    onUse: () -> Unit
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "${candidate.date}（${candidate.daysAgo} 天前）",
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f)
            )
            Spacer(modifier = Modifier.width(8.dp))
            TextButton(onClick = onUse, enabled = enabled) {
                Text("补签")
            }
        }
    }
}

@Composable
private fun HistoryRow(card: MakeupCardHistoryItemUi) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = "${card.grantedMonth} 发放",
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface
                )
                Spacer(modifier = Modifier.height(2.dp))
                Text(
                    text = makeupStatusText(card.status, card.usedForDate),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Text(
                text = if (card.status == "USED") "已使用" else "可用",
                style = MaterialTheme.typography.labelMedium,
                color = if (card.status == "USED") {
                    MaterialTheme.colorScheme.onSurfaceVariant
                } else {
                    MaterialTheme.colorScheme.primary
                }
            )
        }
    }
}

/** 补签卡状态文案（契约 2.8 / 5.4：AVAILABLE / USED）。 */
private fun makeupStatusText(status: String, usedForDate: String?): String = when (status) {
    "USED" -> if (usedForDate.isNullOrBlank()) "已使用" else "已用于补签 $usedForDate"
    "AVAILABLE" -> "未使用，可随时补签"
    else -> status
}
