package com.krisslin.androidaiassistant.feature.interaction

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private const val DISABLED_ALPHA = 0.4f
private const val FALLBACK_ICON = "🎁"

/**
 * 互动菜单底部面板（PRD FR-1 / FR-4 / FR-5 / US-1）。
 *
 * 产品要求（硬性）：**简单平铺列表**，不做分组/分类，也不加“新品”角标；
 * `affordable=false` 的物品整体置灰且不可点击。
 * 实际发送（扣积分 + 调 AI）由 `POST /interaction/send` 负责，本面板只负责选择。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun InteractionMenuSheet(
    state: InteractionMenuUiState,
    onItemSelected: (InteractionMenuItemUi) -> Unit,
    onDismiss: () -> Unit,
    onRetry: () -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = MaterialTheme.colorScheme.surface
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 20.dp)
            ) {
                Text(
                    text = "送点什么给立希？",
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.onSurface
                )
                Spacer(modifier = Modifier.height(4.dp))
                Text(
                    text = "积分余额 ${state.balance}",
                    style = MaterialTheme.typography.labelLarge,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            Spacer(modifier = Modifier.height(8.dp))
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)

            when {
                state.error != null -> InteractionErrorBlock(
                    message = state.error.orEmpty(),
                    onRetry = onRetry
                )

                state.loading && state.items.isEmpty() -> InteractionLoadingBlock()

                state.items.isEmpty() -> InteractionEmptyBlock()

                else -> LazyColumn(modifier = Modifier.fillMaxWidth()) {
                    // PRD FR-5：简单平铺列表，不做分类/新品角标
                    items(state.items, key = { it.id }) { item ->
                        InteractionItemRow(
                            item = item,
                            onClick = { onItemSelected(item) }
                        )
                        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                    }
                }
            }

            Spacer(modifier = Modifier.height(24.dp))
        }
    }
}

@Composable
private fun InteractionItemRow(
    item: InteractionMenuItemUi,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            // US-1：余额不足的物品整体降透明度置灰；enabled=false 保证点击不会触发发送
            .alpha(if (item.affordable) 1f else DISABLED_ALPHA)
            .clickable(enabled = item.affordable, onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        // iconUrl 非空时应渲染远端图标，但本模块未依赖 coil，统一用服务端下发的 emoji 兜底
        Text(
            text = item.icon.ifBlank { FALLBACK_ICON },
            fontSize = 28.sp,
            modifier = Modifier.width(40.dp)
        )
        Spacer(modifier = Modifier.width(8.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = item.name,
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurface
            )
            Spacer(modifier = Modifier.height(2.dp))
            Text(
                text = "消耗 ${item.costPoints} 积分",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        if (item.affordable) {
            TextButton(onClick = onClick) {
                Text("送出")
            }
        } else {
            Text(
                text = "积分不足",
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.error
            )
            TextButton(onClick = onClick, enabled = false) {
                Text("送出")
            }
        }
    }
}

@Composable
private fun InteractionLoadingBlock() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 32.dp),
        contentAlignment = Alignment.Center
    ) {
        CircularProgressIndicator()
    }
}

@Composable
private fun InteractionEmptyBlock() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 32.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = "暂时没有可以送出的礼物",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun InteractionErrorBlock(
    message: String,
    onRetry: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text(
            text = message,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.error
        )
        Spacer(modifier = Modifier.height(8.dp))
        TextButton(onClick = onRetry) {
            Text("重试")
        }
    }
}
