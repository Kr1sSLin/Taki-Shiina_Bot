package com.krisslin.androidaiassistant.feature.interaction

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.hilt.navigation.compose.hiltViewModel

/**
 * 互动入口（PRD FR-1）：主界面右下角常驻“+”悬浮按钮，点击展开互动菜单浮层。
 *
 * 菜单只负责“选择物品”，真正的发送由聊天页处理（保证回复与普通聊天走同一条通道，FR-8）。
 */
@Composable
fun InteractionEntry(
    onSend: (InteractionMenuItemUi) -> Unit,
    modifier: Modifier = Modifier,
    viewModel: InteractionMenuViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsState()
    var expanded by remember { mutableStateOf(false) }

    FloatingActionButton(
        onClick = {
            // 每次打开都刷新余额与可兑换状态（积分随时可能变化）
            viewModel.refresh()
            expanded = true
        },
        modifier = modifier,
        containerColor = MaterialTheme.colorScheme.primaryContainer,
        contentColor = MaterialTheme.colorScheme.onPrimaryContainer
    ) {
        Icon(imageVector = Icons.Filled.Add, contentDescription = "互动菜单")
    }

    if (expanded) {
        InteractionMenuSheet(
            state = state,
            onItemSelected = { item ->
                expanded = false
                onSend(item)
            },
            onDismiss = { expanded = false },
            onRetry = viewModel::refresh
        )
    }
}
