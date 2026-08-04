package com.krisslin.androidaiassistant.feature.chat

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.text.format.DateUtils
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
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
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.hilt.navigation.compose.hiltViewModel
import coil.compose.AsyncImage
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material3.Icon
import com.krisslin.androidaiassistant.core.ui.theme.LocalThemeRevealState
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
    var showEmojiMenu by remember { mutableStateOf(false) }
    val tempCameraUri = remember { mutableStateOf<Uri?>(null) }
    val revealState = LocalThemeRevealState.current
    val keyboardController = LocalSoftwareKeyboardController.current
    var bgIsDark by remember { mutableStateOf(isDarkMode) }
    var bgScreenSize by remember { mutableStateOf(IntSize.Zero) }
    val mediaPermission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        Manifest.permission.READ_MEDIA_IMAGES
    } else {
        Manifest.permission.READ_EXTERNAL_STORAGE
    }
    var hasMediaPermission by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, mediaPermission) == PackageManager.PERMISSION_GRANTED
        )
    }
    val mediaPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasMediaPermission = granted
    }

    LaunchedEffect(revealState.progress) {
        if (revealState.progress >= 1f) {
            bgIsDark = isDarkMode
        }
    }

    val bgBitmap = remember(bgIsDark) {
        BitmapFactory.decodeResource(
            context.resources,
            if (bgIsDark) R.drawable.chat_bg_dark else R.drawable.chat_bg_light
        ).asImageBitmap()
    }
    val targetBgBitmap = remember(isDarkMode) {
        BitmapFactory.decodeResource(
            context.resources,
            if (isDarkMode) R.drawable.chat_bg_dark else R.drawable.chat_bg_light
        ).asImageBitmap()
    }

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
            showAttachMenu = false
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

    var initialScrollSkipped by remember { mutableStateOf(false) }

    LaunchedEffect(state.messages.size) {
        if (!initialScrollSkipped) {
            initialScrollSkipped = true
            return@LaunchedEffect
        }
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.size - 1)
        }
    }

    val lastMessage = state.messages.lastOrNull()
    LaunchedEffect(lastMessage?.content) {
        if (!initialScrollSkipped) {
            initialScrollSkipped = true
            return@LaunchedEffect
        }
        if (state.messages.isNotEmpty()) {
            listState.animateScrollToItem(state.messages.size - 1)
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .onSizeChanged { bgScreenSize = it }
    ) {
        ChatBackground(
            bitmap = bgBitmap,
            targetBitmap = targetBgBitmap,
            bgIsDark = bgIsDark,
            isDarkMode = isDarkMode
        )
        TopDoodleBackground(isDarkMode = isDarkMode)
        Scaffold(
            containerColor = Color.Transparent,
            snackbarHost = { SnackbarHost(snackbarHostState) }
        ) { padding ->
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(padding),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                val statusText = when (state.connectionStatus) {
                    ConnectionStatus.CONNECTING -> "连接中..."
                    ConnectionStatus.CONNECTED -> {
                        val activity = state.botActivity.statusLabel(state.botStage)
                        if (activity != null) "在线 · $activity" else "在线"
                    }
                    ConnectionStatus.DISCONNECTED -> "离线"
                }
                ChatTopBarRow(
                    bgDark = bgIsDark,
                    bgBitmap = bgBitmap,
                    bgScreenSize = bgScreenSize,
                    isDarkMode = isDarkMode,
                    contactName = "Taki",
                    statusText = statusText,
                    onToggleTheme = onToggleTheme,
                    onNavigateToSettings = onNavigateToSettings,
                    onReconnect = viewModel::reconnect
                )

            state.error?.let {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp),
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
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp),
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
                    onPreview = { showImagePreview = it },
                    modifier = Modifier.padding(horizontal = 16.dp)
                )
            }

            val sendEnabled = (state.input.isNotBlank() || state.selectedImages.isNotEmpty()) &&
                state.connectionStatus == ConnectionStatus.CONNECTED
            ChatInputBar(
                bgDark = bgIsDark,
                bgBitmap = bgBitmap,
                bgScreenSize = bgScreenSize,
                input = state.input,
                onInputChange = viewModel::onInputChange,
                enabled = state.connectionStatus == ConnectionStatus.CONNECTED,
                canSend = sendEnabled,
                canAttach = state.connectionStatus == ConnectionStatus.CONNECTED && state.selectedImages.size < 3,
                onSend = viewModel::sendText,
                onAttachClick = {
                    showEmojiMenu = false
                    showAttachMenu = !showAttachMenu
                },
                onEmojiClick = {
                    showAttachMenu = false
                    keyboardController?.hide()
                    showEmojiMenu = !showEmojiMenu
                },
                modifier = Modifier
                    .fillMaxWidth(0.9f)
                    .align(Alignment.CenterHorizontally)
            )
            AnimatedVisibility(
                visible = showEmojiMenu,
                enter = slideInVertically(initialOffsetY = { it }, animationSpec = tween(300)) +
                    expandVertically(expandFrom = Alignment.Bottom, animationSpec = tween(300)) +
                    fadeIn(animationSpec = tween(250)),
                exit = slideOutVertically(targetOffsetY = { it }, animationSpec = tween(240)) +
                    shrinkVertically(shrinkTowards = Alignment.Bottom, animationSpec = tween(240)) +
                    fadeOut(animationSpec = tween(180))
            ) {
                EmojiPickerPanel(
                    bgDark = bgIsDark,
                    bgBitmap = bgBitmap,
                    bgScreenSize = bgScreenSize,
                    onEmojiSelected = { emoji ->
                        viewModel.onInputChange(state.input + emoji)
                    },
                    modifier = Modifier.fillMaxWidth()
                )
            }
            Spacer(modifier = Modifier.height(8.dp))
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

    if (showAttachMenu) {
        MediaPickerSheet(
            isDarkMode = isDarkMode,
            hasPermission = hasMediaPermission,
            onRequestPermission = { mediaPermissionLauncher.launch(mediaPermission) },
            onCameraClick = {
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
            },
            onDismiss = { showAttachMenu = false },
            onConfirm = { uris ->
                showAttachMenu = false
                if (uris.isNotEmpty()) {
                    viewModel.onPickImages(uris.take(3))
                }
            }
        )
    }
}

/**
 * 聊天背景层：底层常驻当前图（bitmap），顶层目标图以切换按钮为圆心圆形扩散。
 *
 * bitmap 与 ChatInputBar 毛玻璃层共享同一 ImageBitmap 与同一裁剪窗口计算，
 * 保证液态玻璃区域与聊天背景逐像素同步；
 * 顶层图先用 clipRect（圆外接矩形）做 scissor 裁剪，再 clipPath 圆，
 * GPU 填充率与圆面积成正比，无需压缩图片分辨率。
 */
@Composable
private fun ChatBackground(
    bitmap: ImageBitmap,
    targetBitmap: ImageBitmap,
    bgIsDark: Boolean,
    isDarkMode: Boolean
) {
    val revealState = LocalThemeRevealState.current
    var bgBoxOrigin by remember { mutableStateOf(Offset.Zero) }
    val circlePath = remember { Path() }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .onGloballyPositioned { bgBoxOrigin = it.boundsInRoot().topLeft }
    ) {
        Image(
            bitmap = bitmap,
            contentDescription = null,
            modifier = Modifier.fillMaxSize(),
            contentScale = ContentScale.Crop
        )
        if (bgIsDark != isDarkMode) {
            Image(
                bitmap = targetBitmap,
                contentDescription = null,
                modifier = Modifier
                    .fillMaxSize()
                    .drawWithContent {
                        val progress = revealState.progress
                        if (progress <= 0f) return@drawWithContent
                        val rawAnchor = revealState.anchor
                        val anchor = if (rawAnchor != null) {
                            rawAnchor - bgBoxOrigin
                        } else {
                            Offset(size.width.toFloat(), 0f)
                        }
                        val maxRadius = maxOf(
                            (anchor - Offset(0f, 0f)).getDistance(),
                            (anchor - Offset(size.width.toFloat(), 0f)).getDistance(),
                            (anchor - Offset(0f, size.height.toFloat())).getDistance(),
                            (anchor - Offset(size.width.toFloat(), size.height.toFloat())).getDistance()
                        ) + 16f
                        val radius = maxRadius * progress
                        if (radius <= 0f) return@drawWithContent
                        val bounds = Rect(center = anchor, radius = radius)
                        circlePath.reset()
                        circlePath.addOval(bounds)
                        clipRect(bounds.left, bounds.top, bounds.right, bounds.bottom) {
                            clipPath(circlePath) {
                                this@drawWithContent.drawContent()
                            }
                        }
                    },
                contentScale = ContentScale.Crop
            )
        }
    }
}

private fun BotActivityStatus.statusLabel(stage: String? = null): String? = when (this) {
    BotActivityStatus.IDLE -> null
    BotActivityStatus.SENDING -> "发送中…"
    BotActivityStatus.TYPING -> when (stage) {
        "vision" -> "看图识物中…"
        "generating" -> "回复生成中…"
        else -> "输入中…"
    }
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
    onPreview: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    LazyRow(
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = modifier
    ) {
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
