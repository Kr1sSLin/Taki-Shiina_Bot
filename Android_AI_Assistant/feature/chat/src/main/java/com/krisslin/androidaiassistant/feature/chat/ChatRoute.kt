package com.krisslin.androidaiassistant.feature.chat

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.text.format.DateUtils
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.hilt.navigation.compose.hiltViewModel
import coil.compose.AsyncImage
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.DarkMode
import androidx.compose.material.icons.filled.LightMode
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Surface
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.vector.ImageVector
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatRoute(
    viewModel: ChatViewModel = hiltViewModel(),
    isDarkMode: Boolean = false,
    onToggleTheme: () -> Unit = {},
    onNavigateToLogin: () -> Unit = {},
    onNavigateToSettings: () -> Unit = {}
) {
    val context = LocalContext.current
    val state by viewModel.uiState.collectAsState()
    val snackbarHostState = remember { SnackbarHostState() }
    val listState = rememberLazyListState()
    var showImagePreview by remember { mutableStateOf<String?>(null) }
    var showAttachMenu by remember { mutableStateOf(false) }
    val tempCameraUri = remember { mutableStateOf<Uri?>(null) }

    val pickImagesLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.GetMultipleContents()
    ) { uris ->
        viewModel.onPickImages(uris.take(3))
    }
    val takePictureLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.TakePicture()
    ) { success ->
        val uri = tempCameraUri.value
        if (success && uri != null) {
            viewModel.onPickImages(listOf(uri))
        }
    }
    val cameraPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (granted) {
            createTempImageUri(context)?.also {
                tempCameraUri.value = it
                takePictureLauncher.launch(it)
            }
        }
    }

    LaunchedEffect(Unit) {
        viewModel.sideEffects.collect { effect ->
            when (effect) {
                is ChatSideEffect.NavigateToLogin -> onNavigateToLogin()
                is ChatSideEffect.ShowToast -> snackbarHostState.showSnackbar(effect.message)
            }
        }
    }

    LaunchedEffect(state.messages.size) {
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.size - 1)
        }
    }

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
                    title = { Text("Taki Shiina") },
                    actions = {
                        IconButton(onClick = onToggleTheme) {
                            Icon(
                                imageVector = if (isDarkMode) Icons.Filled.LightMode else Icons.Filled.DarkMode,
                                contentDescription = if (isDarkMode) "切换日间模式" else "切换夜间模式"
                            )
                        }
                        IconButton(onClick = onNavigateToSettings) {
                            Icon(
                                imageVector = Icons.Filled.Settings,
                                contentDescription = "设置"
                            )
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

            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                state = listState,
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(state.messages, key = { it.id }) { msg ->
                    MessageBubble(
                        message = msg,
                        onImageClick = { showImagePreview = it },
                        onRetry = { viewModel.retryMessage(msg.id) }
                    )
                }
            }

            if (state.selectedImages.isNotEmpty()) {
                SelectedImageStrip(
                    images = state.selectedImages,
                    onRemove = viewModel::removeSelectedImage,
                    onPreview = { showImagePreview = it }
                )
            }

            if (showAttachMenu) {
                Surface(
                    shape = RoundedCornerShape(16.dp),
                    tonalElevation = 2.dp,
                    shadowElevation = 8.dp,
                    modifier = Modifier.align(Alignment.Start)
                ) {
                    Row(
                        modifier = Modifier.padding(16.dp),
                        horizontalArrangement = Arrangement.spacedBy(20.dp)
                    ) {
                        AttachMenuButton(
                            icon = Icons.Outlined.PhotoCamera,
                            label = "相机",
                            onClick = {
                                showAttachMenu = false
                                if (state.selectedImages.size < 3) {
                                    val granted = ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
                                    if (granted) {
                                        createTempImageUri(context)?.also {
                                            tempCameraUri.value = it
                                            takePictureLauncher.launch(it)
                                        }
                                    } else {
                                        cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
                                    }
                                }
                            }
                        )
                        AttachMenuButton(
                            icon = Icons.Outlined.Image,
                            label = "相册",
                            onClick = {
                                showAttachMenu = false
                                pickImagesLauncher.launch("image/*")
                            }
                        )
                    }
                }
            }

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(42.dp)
                        .clip(CircleShape)
                        .background(MaterialTheme.colorScheme.surfaceVariant)
                        .clickable(
                            enabled = state.connectionStatus == ConnectionStatus.CONNECTED && state.selectedImages.size < 3
                        ) { showAttachMenu = !showAttachMenu },
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = Icons.Filled.Add,
                        contentDescription = "添加图片",
                        tint = MaterialTheme.colorScheme.primary
                    )
                }
                OutlinedTextField(
                    value = state.input,
                    onValueChange = viewModel::onInputChange,
                    modifier = Modifier.weight(1f),
                    singleLine = true,
                    enabled = state.connectionStatus == ConnectionStatus.CONNECTED
                )
                val sendEnabled = (state.input.isNotBlank() || state.selectedImages.isNotEmpty()) &&
                    state.connectionStatus == ConnectionStatus.CONNECTED
                Box(
                    modifier = Modifier
                        .size(42.dp)
                        .clip(CircleShape)
                        .background(if (sendEnabled) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant)
                        .clickable(enabled = sendEnabled, onClick = viewModel::sendText),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = Icons.AutoMirrored.Filled.Send,
                        contentDescription = "发送",
                        tint = if (sendEnabled) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier
                            .size(20.dp)
                            .rotate(-45f)
                    )
                }
            }
        }
    }

    showImagePreview?.let { uri ->
        AlertDialog(
            onDismissRequest = { showImagePreview = null },
            title = { Text("图片预览") },
            text = {
                AsyncImage(
                    model = uri,
                    contentDescription = "图片预览",
                    modifier = Modifier.fillMaxWidth()
                )
            },
            confirmButton = {
                TextButton(onClick = { showImagePreview = null }) {
                    Text("关闭")
                }
            }
        )
    }
}

@Composable
private fun AttachMenuButton(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Box(
            modifier = Modifier
                .size(56.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.surfaceVariant)
                .clickable(onClick = onClick),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                imageVector = icon,
                contentDescription = label,
                tint = MaterialTheme.colorScheme.primary
            )
        }
        Text(
            text = label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

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

@Composable
private fun MessageBubble(
    message: ChatMessageUi,
    onImageClick: (String) -> Unit,
    onRetry: () -> Unit
) {
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
                if (message.attachments.isNotEmpty()) {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(message.attachments, key = { it.id }) { attachment ->
                            AsyncImage(
                                model = attachment.localUri,
                                contentDescription = "消息图片",
                                modifier = Modifier
                                    .size(120.dp)
                                    .clip(RoundedCornerShape(8.dp))
                                    .clickable { onImageClick(attachment.localUri) }
                            )
                        }
                    }
                    Spacer(modifier = Modifier.size(6.dp))
                }
                if (message.content.isNotBlank()) {
                    Text(
                        text = message.content.trimEnd('\n'),
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
                if (message.isStreaming) {
                    Text(
                        text = "▌",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.primary
                    )
                }
                if (isUser && message.status == "error") {
                    Spacer(modifier = Modifier.size(6.dp))
                    TextButton(onClick = onRetry) {
                        Text("发送失败，点击重试")
                    }
                }
                Spacer(modifier = Modifier.size(6.dp))
                Text(
                    text = formatMessageTime(message.timestamp),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.align(Alignment.End)
                )
            }
        }
    }
}

private fun formatMessageTime(timestamp: Long): String {
    val pattern = if (DateUtils.isToday(timestamp)) "HH:mm" else "MM-dd HH:mm"
    val formatter = SimpleDateFormat(pattern, Locale.getDefault())
    return formatter.format(Date(timestamp))
}

@Composable
private fun SelectedImageStrip(
    images: List<ChatAttachmentUi>,
    onRemove: (String) -> Unit,
    onPreview: (String) -> Unit
) {
    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(images, key = { it.id }) { item ->
            Box {
                AsyncImage(
                    model = item.localUri,
                    contentDescription = "待发送图片",
                    modifier = Modifier
                        .size(84.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .clickable { onPreview(item.localUri) }
                )
                TextButton(
                    onClick = { onRemove(item.id) },
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .size(22.dp)
                        .background(Color.Black.copy(alpha = 0.45f), CircleShape)
                ) {
                    Text("×", color = Color.White)
                }
            }
        }
    }
}

private fun createTempImageUri(context: Context): Uri? {
    val file = runCatching {
        val dir = java.io.File(context.cacheDir, "camera")
        if (!dir.exists()) dir.mkdirs()
        java.io.File.createTempFile("chat_camera_", ".jpg", dir)
    }.getOrNull() ?: return null
    val authority = "${context.packageName}.fileprovider"
    return FileProvider.getUriForFile(context, authority, file)
}
