package com.krisslin.androidaiassistant

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import androidx.fragment.app.FragmentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.tooling.preview.Preview
import androidx.core.content.ContextCompat
import com.krisslin.androidaiassistant.feature.chat.ChatRoute
import com.krisslin.androidaiassistant.core.ui.theme.AppTheme
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode
import com.krisslin.androidaiassistant.feature.settings.ThemePreferenceStore
import com.krisslin.androidaiassistant.service.WebSocketService
import dagger.hilt.android.AndroidEntryPoint

@AndroidEntryPoint
class MainActivity : FragmentActivity() {

    private var serviceIntent: Intent? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 启动WebSocket前台服务
        serviceIntent = Intent(this, WebSocketService::class.java).apply {
            action = WebSocketService.ACTION_START
        }
        startService(serviceIntent)
        requestNotificationPermissionIfNeeded()
        requestIgnoreBatteryOptimizationIfNeeded()

        setContent {
            val themeStore = remember { ThemePreferenceStore(this) }
            val darkThemeDetected = androidx.compose.foundation.isSystemInDarkTheme()
            AppTheme(
                themeMode = themeStore.themeMode,
                darkThemeDetected = darkThemeDetected
            ) {
                Surface {
                    val effectiveDark = when (themeStore.themeMode) {
                        ThemeMode.SYSTEM -> darkThemeDetected
                        ThemeMode.LIGHT -> false
                        ThemeMode.DARK -> true
                    }
                    AppRoot(
                        isDarkMode = effectiveDark,
                        onToggleTheme = {
                            val nextMode = if (effectiveDark) ThemeMode.LIGHT else ThemeMode.DARK
                            themeStore.updateThemeMode(nextMode)
                        }
                    )
                }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        // 不停止Service，让其独立运行以保持后台连接
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.TIRAMISU) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
            return
        }
        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1001)
    }

    private fun requestIgnoreBatteryOptimizationIfNeeded() {
        if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.M) return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        if (pm.isIgnoringBatteryOptimizations(packageName)) return

        val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
            data = Uri.parse("package:$packageName")
        }
        startActivity(intent)
    }
}

@Composable
private fun AppRoot(
    isDarkMode: Boolean,
    onToggleTheme: () -> Unit
) {
    ChatRoute(
        isDarkMode = isDarkMode,
        onToggleTheme = onToggleTheme
    )
    NotificationGuideDialog()
}

@Composable
private fun NotificationGuideDialog() {
    val context = LocalContext.current
    val prefs = remember {
        context.getSharedPreferences("notification_guide", android.content.Context.MODE_PRIVATE)
    }
    val hasGuided = remember { prefs.getBoolean("has_guided", false) }

    if (hasGuided) return

    var showDialog by remember { mutableStateOf(true) }
    if (!showDialog) return

    AlertDialog(
        onDismissRequest = { },
        title = { Text("开启通知权限") },
        text = {
            Text("为确保您能及时收到消息，请在接下来的设置页面中开启以下权限：\n\n1. 悬浮通知（横幅通知）\n2. 锁屏通知\n\n开启后即可在任何场景下收到消息提醒。")
        },
        confirmButton = {
            TextButton(onClick = {
                prefs.edit().putBoolean("has_guided", true).apply()
                showDialog = false
                val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                    putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                }
                context.startActivity(intent)
            }) {
                Text("去设置")
            }
        },
        dismissButton = {
            TextButton(onClick = {
                prefs.edit().putBoolean("has_guided", true).apply()
                showDialog = false
            }) {
                Text("稍后")
            }
        }
    )
}

@Preview(showBackground = true)
@Composable
private fun PreviewAppRoot() {
    MaterialTheme {
        AppRoot(
            isDarkMode = false,
            onToggleTheme = {}
        )
    }
}
