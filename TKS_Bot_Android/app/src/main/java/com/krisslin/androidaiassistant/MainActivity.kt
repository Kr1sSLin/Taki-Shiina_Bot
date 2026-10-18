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
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import com.krisslin.androidaiassistant.core.push.AppNotifier
import com.krisslin.androidaiassistant.feature.auth.AuthRoute
import com.krisslin.androidaiassistant.feature.chat.ChatRoute
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode
import com.krisslin.androidaiassistant.core.ui.theme.ThemeRevealContainer
import com.krisslin.androidaiassistant.feature.history.NotificationCenterRoute
import com.krisslin.androidaiassistant.feature.profile.MakeupCardRoute
import com.krisslin.androidaiassistant.feature.profile.PointsHistoryRoute
import com.krisslin.androidaiassistant.feature.profile.ProfileRoute
import com.krisslin.androidaiassistant.feature.settings.SettingsRoute
import com.krisslin.androidaiassistant.feature.settings.ThemePreferenceStore
import com.krisslin.androidaiassistant.feature.settings.WeatherSettingsRoute
import com.krisslin.androidaiassistant.service.WebSocketService
import dagger.hilt.android.AndroidEntryPoint
import javax.inject.Inject

@AndroidEntryPoint
class MainActivity : FragmentActivity() {

    private var serviceIntent: Intent? = null
    private var notificationMessageId by mutableStateOf<String?>(null)

    @Inject
    lateinit var appNotifier: AppNotifier

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        notificationMessageId = intent?.getStringExtra("notification_message_id")

        // App 回到前台即清除消息类通知（聊天/问候/错误/提醒），
        // 已读消息不再残留通知栏
        lifecycle.addObserver(object : DefaultLifecycleObserver {
            override fun onStart(owner: LifecycleOwner) {
                appNotifier.setAppForeground(true)
                appNotifier.dismissAll()
            }
            override fun onStop(owner: LifecycleOwner) {
                appNotifier.setAppForeground(false)
            }
        })

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
            val themeMode = themeStore.themeMode
            ThemeRevealContainer(
                themeMode = themeMode,
                targetDark = when (themeMode) {
                    ThemeMode.SYSTEM -> darkThemeDetected
                    ThemeMode.LIGHT -> false
                    ThemeMode.DARK -> true
                }
            ) { effectiveDark ->
                Surface {
                    AppRoot(
                        isDarkMode = effectiveDark,
                        targetMessageId = notificationMessageId,
                        onToggleTheme = {
                            val nextMode = if (effectiveDark) ThemeMode.LIGHT else ThemeMode.DARK
                            themeStore.updateThemeMode(nextMode)
                        }
                    )
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        notificationMessageId = intent.getStringExtra("notification_message_id")
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
    targetMessageId: String? = null,
    onToggleTheme: () -> Unit
) {
    val mainViewModel: MainViewModel = hiltViewModel()
    val tokenState by mainViewModel.tokenState.collectAsState()
    var mainPage by rememberSaveable { mutableStateOf(MainPage.CHAT) }
    // 页面级状态容器：页面切换（如聊天页→设置页）时保存各页状态（列表滚动位置等），返回时恢复
    val pageStateHolder = rememberSaveableStateHolder()

    // 登出后重置回聊天页，避免重新登录落在设置页
    LaunchedEffect(tokenState) {
        if (tokenState is TokenState.Unauthenticated) mainPage = MainPage.CHAT
    }
    LaunchedEffect(targetMessageId) {
        if (targetMessageId != null) mainPage = MainPage.CHAT
    }

    when (tokenState) {
        // 未登录/会话失效：展示登录页（登录成功后 tokenState 变化，自动切回聊天页）
        is TokenState.Unauthenticated -> AuthRoute()
        is TokenState.Authenticated -> when (mainPage) {
            MainPage.CHAT -> pageStateHolder.SaveableStateProvider(MainPage.CHAT) {
                ChatRoute(
                    targetMessageId = targetMessageId,
                    isDarkMode = isDarkMode,
                    onToggleTheme = onToggleTheme,
                    onNavigateToSettings = { mainPage = MainPage.SETTINGS },
                    onNavigateToProfile = { mainPage = MainPage.PROFILE }
                )
            }
            // ===== 互动积分 · 等级体系（PRD §3.1 feature:profile） =====
            MainPage.PROFILE -> pageStateHolder.SaveableStateProvider(MainPage.PROFILE) {
                BackHandler { mainPage = MainPage.CHAT }
                ProfileRoute(
                    onBack = { mainPage = MainPage.CHAT },
                    onNavigateToHistory = { mainPage = MainPage.POINTS_HISTORY },
                    onNavigateToMakeupCard = { mainPage = MainPage.MAKEUP_CARD }
                )
            }
            MainPage.POINTS_HISTORY -> pageStateHolder.SaveableStateProvider(MainPage.POINTS_HISTORY) {
                BackHandler { mainPage = MainPage.PROFILE }
                PointsHistoryRoute(onBack = { mainPage = MainPage.PROFILE })
            }
            MainPage.MAKEUP_CARD -> pageStateHolder.SaveableStateProvider(MainPage.MAKEUP_CARD) {
                BackHandler { mainPage = MainPage.PROFILE }
                MakeupCardRoute(onBack = { mainPage = MainPage.PROFILE })
            }
            MainPage.SETTINGS -> pageStateHolder.SaveableStateProvider(MainPage.SETTINGS) {
                BackHandler { mainPage = MainPage.CHAT }
                SettingsRoute(
                    onBack = { mainPage = MainPage.CHAT },
                    onNavigateToWeather = { mainPage = MainPage.WEATHER_SETTINGS },
                    onNavigateToNotificationCenter = { mainPage = MainPage.NOTIFICATION_CENTER }
                )
            }
            // ===== 通知中心（原错误中心，feature:history）=====
            // 返回落在设置页而非聊天页：入口在设置页，返回必须回到来源，否则用户会「穿过」设置页
            MainPage.NOTIFICATION_CENTER -> pageStateHolder.SaveableStateProvider(MainPage.NOTIFICATION_CENTER) {
                BackHandler { mainPage = MainPage.SETTINGS }
                NotificationCenterRoute(onBack = { mainPage = MainPage.SETTINGS })
            }
            MainPage.WEATHER_SETTINGS -> pageStateHolder.SaveableStateProvider(MainPage.WEATHER_SETTINGS) {
                BackHandler { mainPage = MainPage.SETTINGS }
                WeatherSettingsRoute(
                    onBack = { mainPage = MainPage.SETTINGS }
                )
            }
        }
    }
    NotificationGuideDialog()
}

private object MainPage {
    const val CHAT = "chat"
    const val SETTINGS = "settings"
    const val WEATHER_SETTINGS = "weather_settings"
    const val PROFILE = "profile"
    const val POINTS_HISTORY = "points_history"
    const val MAKEUP_CARD = "makeup_card"
    const val NOTIFICATION_CENTER = "notification_center"
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
            Text("你可以在系统设置中允许消息通知。关闭通知权限也不会删除聊天消息；下次打开应用仍可同步收件箱。后台提醒是否送达取决于设备和系统设置。")
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
