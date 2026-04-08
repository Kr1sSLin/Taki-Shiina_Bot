package com.krisslin.androidaiassistant.feature.history

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@Composable
fun HistoryRoute(viewModel: HistoryViewModel = hiltViewModel()) {
    val state by viewModel.uiState.collectAsState()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "错误中心（未读 ${state.unreadCount}）",
                style = MaterialTheme.typography.titleLarge
            )
            TextButton(onClick = viewModel::markAllAsRead) {
                Text("全部已读")
            }
        }

        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            item {
                Text(
                    text = "用户客观事实",
                    style = MaterialTheme.typography.titleMedium
                )
            }

            items(state.userFacts, key = { it.factId }) { item ->
                Card(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(12.dp)) {
                        Text(text = item.fact, style = MaterialTheme.typography.bodyMedium)
                        Text(
                            text = formatTimestamp(item.timestamp),
                            style = MaterialTheme.typography.labelSmall
                        )
                    }
                }
            }

            item {
                Text(
                    text = "Bot 错误通知",
                    style = MaterialTheme.typography.titleMedium
                )
            }

            items(state.notifications, key = { it.notificationId }) { item ->
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { viewModel.markAsRead(item.notificationId) }
                ) {
                    Column(modifier = Modifier.padding(12.dp)) {
                        Text(text = item.errorCode, style = MaterialTheme.typography.titleSmall)
                        Text(text = item.message, style = MaterialTheme.typography.bodyMedium)
                        Text(
                            text = if (item.isRead == 0) "未读" else "已读",
                            style = MaterialTheme.typography.labelSmall
                        )
                    }
                }
            }
        }
    }
}

private fun formatTimestamp(timestamp: Long): String {
    val formatter = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
    return formatter.format(Date(timestamp))
}
