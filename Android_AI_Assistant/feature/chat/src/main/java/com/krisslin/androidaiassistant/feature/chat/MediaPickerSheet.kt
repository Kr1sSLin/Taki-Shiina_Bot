package com.krisslin.androidaiassistant.feature.chat

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.CameraAlt
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import coil.compose.AsyncImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

data class MediaItem(val uri: Uri, val id: Long)

private const val MAX_SELECTION = 3
private const val QUERY_LIMIT = 120

private suspend fun queryRecentImages(context: Context, limit: Int = QUERY_LIMIT): List<MediaItem> =
    withContext(Dispatchers.IO) {
        val collection = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        }
        val projection = arrayOf(
            MediaStore.Images.Media._ID,
            MediaStore.Images.Media.DATE_ADDED
        )
        val sortOrder = "${MediaStore.Images.Media.DATE_ADDED} DESC"
        val result = mutableListOf<MediaItem>()
        runCatching {
            context.contentResolver.query(collection, projection, null, null, sortOrder)?.use { cursor ->
                val idColumn = cursor.getColumnIndexOrThrow(MediaStore.Images.Media._ID)
                while (cursor.moveToNext() && result.size < limit) {
                    val id = cursor.getLong(idColumn)
                    result.add(MediaItem(ContentUris.withAppendedId(collection, id), id))
                }
            }
        }
        result
    }

/**
 * Telegram 风格应用内照片选择器：
 * - 玻璃拟态圆角面板从底部滑出，背景与文字随日/夜间模式联动（透出紫色壁纸）
 * - 三列照片网格 + 相机快捷入口
 * - 多选圆形序号指示器（最多 3 张）
 * - 底部确认按钮与聊天框发送按钮同款（紫色圆形 + 按压放大）
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MediaPickerSheet(
    isDarkMode: Boolean,
    hasPermission: Boolean,
    onRequestPermission: () -> Unit,
    onCameraClick: () -> Unit,
    onDismiss: () -> Unit,
    onConfirm: (List<Uri>) -> Unit
) {
    val context = LocalContext.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    var images by remember { mutableStateOf<List<MediaItem>>(emptyList()) }
    val selected = remember { mutableStateListOf<Uri>() }

    val sendInteraction = remember { MutableInteractionSource() }
    val sendPressed by sendInteraction.collectIsPressedAsState()
    val sendScale by animateFloatAsState(
        targetValue = if (sendPressed) 0.88f else 1f,
        label = "sendScale"
    )

    LaunchedEffect(hasPermission) {
        if (hasPermission) {
            images = queryRecentImages(context)
        }
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        containerColor = if (isDarkMode) {
            Color(0xFF14141F).copy(alpha = 0.94f)
        } else {
            Color.White.copy(alpha = 0.93f)
        },
        scrimColor = Color.Black.copy(alpha = 0.35f),
        dragHandle = {
            Box(
                modifier = Modifier
                    .padding(top = 10.dp, bottom = 4.dp)
                    .width(36.dp)
                    .height(4.dp)
                    .clip(CircleShape)
                    .background(
                        if (isDarkMode) Color.White.copy(alpha = 0.2f)
                        else Color.Black.copy(alpha = 0.15f)
                    )
            )
        }
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 20.dp, end = 12.dp, top = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                if (selected.isNotEmpty()) {
                    Text(
                        text = "已选 ${selected.size}/$MAX_SELECTION",
                        style = MaterialTheme.typography.labelMedium,
                        color = Color(0xFFB04CFF)
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                }
                Spacer(modifier = Modifier.weight(1f))
                IconButton(onClick = onDismiss) {
                    Icon(
                        imageVector = Icons.Filled.Close,
                        contentDescription = "关闭",
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            if (!hasPermission) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 32.dp, vertical = 48.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        text = "需要相册权限才能选择照片",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(modifier = Modifier.height(16.dp))
                    Button(
                        onClick = onRequestPermission,
                        shape = RoundedCornerShape(24.dp),
                        colors = androidx.compose.material3.ButtonDefaults.buttonColors(
                            containerColor = Color(0xFFB04CFF)
                        )
                    ) {
                        Text("授权访问相册")
                    }
                }
            } else if (images.isEmpty()) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 48.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        text = "相册中没有照片",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            } else {
                LazyVerticalGrid(
                    columns = GridCells.Fixed(3),
                    modifier = Modifier
                        .fillMaxWidth()
                        .fillMaxHeight(0.62f),
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                    verticalArrangement = Arrangement.spacedBy(2.dp)
                ) {
                    item(key = "camera_tile") {
                        CameraTile(onClick = onCameraClick)
                    }
                    items(images, key = { it.id }) { media ->
                        PhotoGridItem(
                            media = media,
                            order = selected.indexOf(media.uri) + 1,
                            isSelected = selected.contains(media.uri),
                            canSelect = selected.size < MAX_SELECTION || selected.contains(media.uri),
                            isDarkMode = isDarkMode,
                            onToggle = {
                                if (selected.contains(media.uri)) {
                                    selected.remove(media.uri)
                                } else if (selected.size < MAX_SELECTION) {
                                    selected.add(media.uri)
                                }
                            }
                        )
                    }
                }
            }

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 20.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                if (selected.isNotEmpty()) {
                    Text(
                        text = "已选 ${selected.size} 张",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    Spacer(modifier = Modifier.weight(1f))
                } else {
                    Spacer(modifier = Modifier.weight(1f))
                }
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .graphicsLayer {
                            scaleX = sendScale
                            scaleY = sendScale
                        }
                        .clip(CircleShape)
                        .background(
                            if (selected.isNotEmpty()) Color(0xFFB04CFF)
                            else if (isDarkMode) Color(0xFF3A3A4A) else Color(0xFFD9D9D9)
                        )
                        .clickable(
                            interactionSource = sendInteraction,
                            indication = null,
                            enabled = selected.isNotEmpty(),
                            onClick = { onConfirm(selected.toList()) }
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = Icons.AutoMirrored.Filled.Send,
                        contentDescription = "发送",
                        tint = Color.White,
                        modifier = Modifier
                            .size(20.dp)
                            .rotate(-45f)
                    )
                }
            }
        }
    }
}

@Composable
private fun CameraTile(onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .clip(RoundedCornerShape(10.dp))
            .background(
                Brush.linearGradient(
                    listOf(
                        Color(0xFFC86DFF),
                        Color(0xFFB04CFF)
                    )
                )
            )
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Icon(
                imageVector = Icons.Filled.CameraAlt,
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.size(30.dp)
            )
            Text(
                text = "相机",
                style = MaterialTheme.typography.labelMedium,
                color = Color.White
            )
        }
    }
}

@Composable
private fun PhotoGridItem(
    media: MediaItem,
    order: Int,
    isSelected: Boolean,
    canSelect: Boolean,
    isDarkMode: Boolean,
    onToggle: () -> Unit
) {
    Box(
        modifier = Modifier
            .aspectRatio(1f)
            .clip(RoundedCornerShape(10.dp))
            .clickable(enabled = canSelect, onClick = onToggle)
    ) {
        AsyncImage(
            model = media.uri,
            contentDescription = null,
            modifier = Modifier.fillMaxSize(),
            contentScale = ContentScale.Crop
        )
        if (isSelected) {
            Box(
                modifier = Modifier
                    .matchParentSize()
                    .background(Color.Black.copy(alpha = 0.25f))
            )
        }
        Box(
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(6.dp)
                .size(24.dp)
                .clip(CircleShape)
                .background(
                    if (isSelected) Color(0xFFB04CFF)
                    else if (isDarkMode) Color.Black.copy(alpha = 0.4f) else Color.White.copy(alpha = 0.75f)
                )
                .border(
                    width = 1.5.dp,
                    color = if (isSelected) Color.White
                    else if (isDarkMode) Color.White.copy(alpha = 0.4f) else Color.Black.copy(alpha = 0.25f),
                    shape = CircleShape
                ),
            contentAlignment = Alignment.Center
        ) {
            if (isSelected) {
                Text(
                    text = order.toString(),
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White
                )
            }
        }
    }
}
