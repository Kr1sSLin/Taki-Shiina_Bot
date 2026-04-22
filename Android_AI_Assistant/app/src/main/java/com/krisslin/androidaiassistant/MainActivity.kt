package com.krisslin.androidaiassistant

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.fragment.app.FragmentActivity
import androidx.activity.compose.setContent
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.draw.blur
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.krisslin.androidaiassistant.feature.chat.ChatRoute
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
            MaterialTheme {
                Surface {
                    AppRoot(activity = this)
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
private fun AppRoot(activity: FragmentActivity?) {
    var lockState by remember { mutableStateOf(AppLockState.LOCKED) }
    var authError by remember { mutableStateOf<String?>(null) }
    val lifecycleOwner = LocalLifecycleOwner.current

    fun triggerBiometricAuth() {
        val hostActivity = activity ?: run {
            lockState = AppLockState.LOCKED
            authError = "无法启动生物识别验证"
            return
        }

        val biometricManager = BiometricManager.from(hostActivity)
        val authenticators = BiometricManager.Authenticators.BIOMETRIC_STRONG or
            BiometricManager.Authenticators.BIOMETRIC_WEAK

        when (biometricManager.canAuthenticate(authenticators)) {
            BiometricManager.BIOMETRIC_SUCCESS -> {
                val executor = ContextCompat.getMainExecutor(hostActivity)
                val callback = object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                        lockState = AppLockState.UNLOCKED
                        authError = null
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                        lockState = AppLockState.LOCKED
                        authError = when (errorCode) {
                            BiometricPrompt.ERROR_USER_CANCELED,
                            BiometricPrompt.ERROR_NEGATIVE_BUTTON,
                            BiometricPrompt.ERROR_CANCELED -> "认证已取消，请重试"
                            BiometricPrompt.ERROR_LOCKOUT,
                            BiometricPrompt.ERROR_LOCKOUT_PERMANENT -> "验证次数过多，请稍后重试"
                            else -> errString.toString()
                        }
                    }

                    override fun onAuthenticationFailed() {
                        authError = "指纹不匹配，请重试"
                    }
                }

                val prompt = BiometricPrompt(hostActivity, executor, callback)
                val promptInfo = BiometricPrompt.PromptInfo.Builder()
                    .setTitle("身份验证")
                    .setSubtitle("请验证指纹以解锁应用")
                    .setNegativeButtonText("取消")
                    .build()
                prompt.authenticate(promptInfo)
            }
            BiometricManager.BIOMETRIC_ERROR_NONE_ENROLLED -> {
                lockState = AppLockState.LOCKED
                authError = "设备未录入指纹，请先在系统设置中添加指纹"
            }
            BiometricManager.BIOMETRIC_ERROR_NO_HARDWARE,
            BiometricManager.BIOMETRIC_ERROR_HW_UNAVAILABLE -> {
                lockState = AppLockState.LOCKED
                authError = "当前设备不支持或暂不可用生物识别"
            }
            else -> {
                lockState = AppLockState.LOCKED
                authError = "生物识别不可用"
            }
        }
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                lockState = AppLockState.LOCKED
                triggerBiometricAuth()
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
        }
    }

    Box(modifier = Modifier.fillMaxSize()) {
        val blurRadius by animateDpAsState(
            targetValue = if (lockState == AppLockState.LOCKED) 18.dp else 0.dp,
            label = "lock_blur_radius"
        )

        Box(
            modifier = Modifier
                .fillMaxSize()
                .blur(blurRadius)
        ) {
            ChatRoute()
        }

        if (lockState == AppLockState.LOCKED) {
            BackHandler(enabled = true) {}
            LockOverlay(
                errorMessage = authError,
                onRetry = { triggerBiometricAuth() }
            )
        }

        // 首次安装引导开启通知权限
        if (lockState == AppLockState.UNLOCKED) {
            NotificationGuideDialog()
        }
    }
}

private enum class AppLockState {
    LOCKED,
    UNLOCKED
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

@Composable
private fun LockOverlay(
    errorMessage: String?,
    onRetry: () -> Unit
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xEE0F1115))
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
                onClick = {}
            )
    ) {
        Column(
            modifier = Modifier
                .align(Alignment.Center)
                .padding(horizontal = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "应用已锁定",
                style = MaterialTheme.typography.titleLarge,
                color = Color.White
            )
            Text(
                text = "请使用指纹验证后继续",
                style = MaterialTheme.typography.bodyMedium,
                color = Color.White.copy(alpha = 0.85f)
            )
            if (!errorMessage.isNullOrBlank()) {
                Text(
                    text = errorMessage,
                    color = Color(0xFFFFB4AB),
                    fontSize = 14.sp
                )
            }
            TextButton(onClick = onRetry) {
                Text(text = "重新验证")
            }
        }
    }
}

@Preview(showBackground = true)
@Composable
private fun PreviewAppRoot() {
    MaterialTheme {
        AppRoot(activity = null)
    }
}
