package com.krisslin.androidaiassistant.feature.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.OutlinedButton
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode

@Composable
fun SettingsRoute(viewModel: SettingsViewModel = hiltViewModel()) {
    val state by viewModel.uiState.collectAsState()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text(
            text = "城市设置",
            style = MaterialTheme.typography.titleLarge
        )

        Text(
            text = "主题模式",
            style = MaterialTheme.typography.titleMedium
        )

        val themeMode = state.themeMode
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            ThemeMode.values().forEach { mode ->
                OutlinedButton(
                    onClick = { viewModel.setThemeMode(mode) },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        text = when (mode) {
                            ThemeMode.SYSTEM -> "跟随系统"
                            ThemeMode.LIGHT -> "日间模式"
                            ThemeMode.DARK -> "夜间模式"
                        } + if (mode == themeMode) " · 已选中" else ""
                    )
                }
            }
        }

        OutlinedTextField(
            value = state.cityInput,
            onValueChange = viewModel::onCityInputChange,
            label = { Text("城市（或纬度,经度）") },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            enabled = !state.loading
        )

        Button(
            onClick = viewModel::saveCity,
            enabled = !state.loading,
            modifier = Modifier.fillMaxWidth()
        ) {
            if (state.loading) {
                CircularProgressIndicator(strokeWidth = 2.dp)
            } else {
                Text("保存")
            }
        }

        state.message?.let {
            Text(text = it, style = MaterialTheme.typography.bodyMedium)
            LaunchedEffect(it) {
                // 保持现有提示一段时间后可由用户手动覆盖，不自动清空
            }
        }
    }
}
