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
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel

/**
 * 「我的陪伴」页（PRD FR-14 / FR-15 / FR-19）。
 *
 * 内容：熊猫成长等级卡片（含升级进度条）、积分余额（入口：积分流水）、
 * 补签卡（入口：补签）、等级说明表。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProfileRoute(
    onBack: () -> Unit,
    onNavigateToHistory: () -> Unit,
    onNavigateToMakeupCard: () -> Unit,
    viewModel: ProfileViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()

    // 首屏拉取一次（本期不做下拉刷新）；手动刷新见右上角按钮
    LaunchedEffect(Unit) {
        viewModel.refresh()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("我的陪伴") },
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
        }
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            when {
                // 首次加载：避免用空数据闪一屏
                state.loading && state.continuousDays == 0 && state.levelTable.isEmpty() ->
                    CircularProgressIndicator(modifier = Modifier.align(Alignment.Center))

                // 无缓存可展示的失败态
                state.error != null && state.continuousDays == 0 && state.levelTable.isEmpty() ->
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

                else -> ProfileContent(
                    state = state,
                    onNavigateToHistory = onNavigateToHistory,
                    onNavigateToMakeupCard = onNavigateToMakeupCard,
                    onRetry = viewModel::refresh
                )
            }
        }
    }
}

@Composable
private fun ProfileContent(
    state: ProfileUiState,
    onNavigateToHistory: () -> Unit,
    onNavigateToMakeupCard: () -> Unit,
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
                ErrorBanner(message = error, onRetry = onRetry)
            }
        }

        item(key = "level_card") {
            LevelCard(state = state)
        }

        item(key = "points") {
            PointsRow(
                balance = state.balance,
                onNavigateToHistory = onNavigateToHistory
            )
        }

        item(key = "makeup_card") {
            MakeupCardRow(
                available = state.availableMakeupCards,
                max = state.makeupCardMax,
                onNavigateToMakeupCard = onNavigateToMakeupCard
            )
        }

        item(key = "level_table_title") {
            Text(
                text = "等级说明",
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
        }

        if (state.levelTable.isEmpty()) {
            item(key = "level_table_empty") {
                Text(
                    text = "暂无等级配置",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        } else {
            // 不指定 key：等级表是静态小列表，用位置索引作 key 可避免服务端字段异常时
            // 出现重复 key 直接崩溃（LazyColumn 对重复 key 会抛 IllegalArgumentException）
            items(state.levelTable) { item ->
                LevelThresholdRow(item = item)
            }
        }
    }
}

@Composable
private fun LevelCard(state: ProfileUiState) {
    // PRD FR-15 / EDGE-7：NONE 默认态不设“断签回落”专属文案，与从未升级的新用户共用同一套展示
    val title = if (state.levelCode == "NONE" || state.levelName.isBlank()) {
        "还没有等级称号"
    } else {
        state.levelName
    }

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
                text = "熊猫成长等级",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(modifier = Modifier.height(6.dp))
            Text(
                text = title,
                style = MaterialTheme.typography.headlineSmall,
                color = MaterialTheme.colorScheme.primary
            )
            Spacer(modifier = Modifier.height(4.dp))
            Text(
                text = "连续陪伴 ${state.continuousDays} 天",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface
            )
            Spacer(modifier = Modifier.height(12.dp))
            LinearProgressIndicator(
                progress = { levelProgress(state) },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(8.dp)
                    .clip(RoundedCornerShape(4.dp)),
                color = MaterialTheme.colorScheme.primary,
                trackColor = MaterialTheme.colorScheme.surfaceVariant
            )
            Spacer(modifier = Modifier.height(8.dp))
            Text(
                text = if (state.nextLevelName == null) {
                    "已是最高等级 传奇熊猫"
                } else {
                    "距 ${state.nextLevelName} 还差 ${state.daysToNextLevel ?: 0} 天"
                },
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

/** 升级进度 =（当前连续天数 − 当前等级阈值）/（下一等级阈值 − 当前等级阈值）。 */
private fun levelProgress(state: ProfileUiState): Float {
    val nextThreshold = state.nextLevelThresholdDays ?: return 1f
    val currentThreshold = state.levelTable
        .firstOrNull { it.levelCode == state.levelCode }
        ?.thresholdDays
        ?: 0
    if (nextThreshold <= currentThreshold) return 0f
    val ratio = (state.continuousDays - currentThreshold).toFloat() /
        (nextThreshold - currentThreshold).toFloat()
    return ratio.coerceIn(0f, 1f)
}

@Composable
private fun PointsRow(
    balance: Int,
    onNavigateToHistory: () -> Unit
) {
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
                    text = "积分余额",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Text(
                    text = balance.toString(),
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            TextButton(onClick = onNavigateToHistory) {
                Text("查看流水")
            }
        }
    }
}

@Composable
private fun MakeupCardRow(
    available: Int,
    max: Int,
    onNavigateToMakeupCard: () -> Unit
) {
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
                    text = "补签卡",
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Text(
                    text = "可用 $available/$max 张",
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurface
                )
            }
            TextButton(onClick = onNavigateToMakeupCard) {
                Text("去补签")
            }
        }
    }
}

@Composable
private fun LevelThresholdRow(item: LevelThresholdUi) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 4.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(
            imageVector = Icons.Filled.Check,
            contentDescription = null,
            tint = if (item.reached) {
                MaterialTheme.colorScheme.primary
            } else {
                MaterialTheme.colorScheme.outlineVariant
            }
        )
        Spacer(modifier = Modifier.width(12.dp))
        Text(
            text = item.levelName,
            style = MaterialTheme.typography.bodyLarge,
            color = if (item.reached) {
                MaterialTheme.colorScheme.onSurface
            } else {
                MaterialTheme.colorScheme.onSurfaceVariant
            },
            modifier = Modifier.weight(1f)
        )
        Text(
            text = if (item.reached) "已达成 · 连续 ${item.thresholdDays} 天" else "连续 ${item.thresholdDays} 天",
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun ErrorBanner(
    message: String,
    onRetry: () -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = message,
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.error,
            modifier = Modifier.weight(1f)
        )
        TextButton(onClick = onRetry) {
            Text("重试")
        }
    }
}
