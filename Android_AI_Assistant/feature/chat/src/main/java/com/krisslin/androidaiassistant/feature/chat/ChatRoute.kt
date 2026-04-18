package com.krisslin.androidaiassistant.feature.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatRoute(
    viewModel: ChatViewModel = hiltViewModel(),
    onNavigateToLogin: () -> Unit = {}
) {
    val state by viewModel.uiState.collectAsState()
    val snackbarHostState = remember { SnackbarHostState() }
    val listState = rememberLazyListState()
    var showClearConfirm by remember { mutableStateOf(false) }

    // 处理 side effects
    LaunchedEffect(Unit) {
        viewModel.sideEffects.collect { effect ->
            when (effect) {
                is ChatSideEffect.NavigateToLogin -> onNavigateToLogin()
                is ChatSideEffect.ShowToast -> snackbarHostState.showSnackbar(effect.message)
            }
        }
    }

    // 自动滚动到底部
    LaunchedEffect(state.messages.size) {
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.size - 1)
        }
    }

    // 流式消息更新时也滚动
    val lastMessage = state.messages.lastOrNull()
    LaunchedEffect(lastMessage?.content) {
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.size - 1)
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            Column {
                TopAppBar(
                    title = { Text("对话") },
                    actions = {
                        TextButton(onClick = { showClearConfirm = true }) {
                            Text("清空会话")
                        }
                    }
                )
                ConnectionStatusBar(
                    status = state.connectionStatus,
                    botActivity = state.botActivity,
                    onReconnect = viewModel::reconnect
                )
            }
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // 提示信息区域
            BotActivityHint(state.botActivity)
            state.error?.let {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(text = "错误：$it", color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = viewModel::clearError) {
                        Text("关闭")
                    }
                }
            }

            // 消息列表
            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                state = listState,
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(state.messages, key = { it.id }) { msg ->
                    MessageBubble(message = msg)
                }
            }

            // 输入区域
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                OutlinedTextField(
                    value = state.input,
                    onValueChange = viewModel::onInputChange,
                    modifier = Modifier.weight(1f),
                    placeholder = { Text("输入消息") },
                    singleLine = true,
                    enabled = state.connectionStatus == ConnectionStatus.CONNECTED
                )
                Button(
                    onClick = viewModel::sendText,
                    enabled = state.input.isNotBlank()
                        && state.connectionStatus == ConnectionStatus.CONNECTED
                ) {
                    Text("发送")
                }
            }
        }
    }

    if (showClearConfirm) {
        AlertDialog(
            onDismissRequest = { showClearConfirm = false },
            title = { Text("清空会话") },
            text = { Text("仅清空本地聊天记录，不影响登录状态。是否继续？") },
            confirmButton = {
                TextButton(
                    onClick = {
                        showClearConfirm = false
                        viewModel.clearConversation()
                    }
                ) {
                    Text("清空")
                }
            },
            dismissButton = {
                TextButton(onClick = { showClearConfirm = false }) {
                    Text("取消")
                }
            }
        )
    }
}

/**
 * 连接状态栏
 */
@Composable
private fun ConnectionStatusBar(
    status: ConnectionStatus,
    botActivity: BotActivityStatus,
    onReconnect: () -> Unit
) {
    val (statusText, statusColor) = when (status) {
        ConnectionStatus.CONNECTING -> "连接中..." to Color(0xFFFFA726)
        ConnectionStatus.CONNECTED -> "在线" to Color(0xFF4CAF50)
        ConnectionStatus.DISCONNECTED -> "离线" to Color(0xFFF44336)
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surface)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (status == ConnectionStatus.CONNECTING) {
                CircularProgressIndicator(
                    modifier = Modifier.size(12.dp),
                    strokeWidth = 2.dp,
                    color = statusColor
                )
            } else {
                Box(
                    modifier = Modifier
                        .size(10.dp)
                        .clip(CircleShape)
                        .background(statusColor)
                )
            }
            Spacer(modifier = Modifier.width(8.dp))
            Text(
                text = if (status == ConnectionStatus.CONNECTED && botActivity != BotActivityStatus.IDLE) {
                    "$statusText · ${botActivity.statusLabel()}"
                } else {
                    statusText
                },
                style = MaterialTheme.typography.titleMedium
            )
        }
        
        if (status == ConnectionStatus.DISCONNECTED) {
            TextButton(onClick = onReconnect) {
                Text("重连")
            }
        }
    }
}

@Composable
private fun BotActivityHint(activity: BotActivityStatus) {
    val label = activity.statusLabel() ?: return
    Text(text = label, color = MaterialTheme.colorScheme.primary)
}

private fun BotActivityStatus.statusLabel(): String? = when (this) {
    BotActivityStatus.IDLE -> null
    BotActivityStatus.SENDING -> "发送中…"
    BotActivityStatus.TYPING -> "输入中…"
}

/**
 * 消息气泡
 */
@Composable
private fun MessageBubble(message: ChatMessageUi) {
    val isUser = message.role == "user"
    val alignment = if (isUser) Alignment.End else Alignment.Start
    val bubbleColor = if (isUser) {
        MaterialTheme.colorScheme.primaryContainer
    } else {
        MaterialTheme.colorScheme.secondaryContainer
    }

    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = alignment
    ) {
        Card(
            shape = RoundedCornerShape(
                topStart = 16.dp,
                topEnd = 16.dp,
                bottomStart = if (isUser) 16.dp else 4.dp,
                bottomEnd = if (isUser) 4.dp else 16.dp
            ),
            colors = CardDefaults.cardColors(containerColor = bubbleColor)
        ) {
            Column(modifier = Modifier.padding(12.dp)) {
                Text(
                    text = message.content.trimEnd('\n'),
                    style = MaterialTheme.typography.bodyMedium
                )
                if (message.isStreaming) {
                    Text(
                        text = "▌",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.primary
                    )
                }
            }
        }
    }
}
