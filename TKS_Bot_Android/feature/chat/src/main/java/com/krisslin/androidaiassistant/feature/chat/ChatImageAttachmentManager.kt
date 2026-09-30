package com.krisslin.androidaiassistant.feature.chat

import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Base64
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.ContentType
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

internal data class OutgoingImage(val ui: ChatAttachmentUi, val dataBase64: String)
internal data class OutgoingPayload(val text: String, val images: List<OutgoingImage>)
internal data class AttachmentResult<T>(val value: T? = null, val error: String? = null)

/** Owns image validation, private-file persistence, encoding, and legacy attachment repair. */
internal class ChatImageAttachmentManager(
    private val context: Context,
    private val chatRepository: ChatRepository,
    private val sessionId: String
) {
    companion object {
        private const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
        const val MAX_IMAGE_TIPS = "图片超过 20MB 限制"
    }

    suspend fun prepare(text: String, selectedImages: List<ChatAttachmentUi>): AttachmentResult<OutgoingPayload> {
        val encoded = mutableListOf<OutgoingImage>()
        for (item in selectedImages.take(3)) {
            val uri = runCatching { Uri.parse(item.localUri) }.getOrNull() ?: continue
            val bytes = runCatching { context.contentResolver.openInputStream(uri)?.use { it.readBytes() } }.getOrNull()
                ?: return AttachmentResult(error = "读取图片失败")
            if (bytes.size > MAX_IMAGE_BYTES) return AttachmentResult(error = MAX_IMAGE_TIPS)
            encoded += OutgoingImage(item, Base64.encodeToString(bytes, Base64.NO_WRAP))
        }
        if (encoded.isEmpty() && text.isBlank()) return AttachmentResult(error = "消息内容不能为空")
        return AttachmentResult(value = OutgoingPayload(text, encoded))
    }

    fun createUiAttachment(uri: Uri): AttachmentResult<ChatAttachmentUi> {
        val resolver = context.contentResolver
        val mimeType = resolver.getType(uri) ?: "image/jpeg"
        if (mimeType != "image/jpeg" && mimeType != "image/png") return AttachmentResult(error = "仅支持 JPG/PNG")
        val fileSize = resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getLong(0) else -1L
        } ?: -1L
        if (fileSize > MAX_IMAGE_BYTES) return AttachmentResult(error = MAX_IMAGE_TIPS)
        val bounds = resolver.openInputStream(uri)?.use { input ->
            BitmapFactory.Options().apply {
                inJustDecodeBounds = true
                BitmapFactory.decodeStream(input, null, this)
            }
        }
        val persistedUri = copyToPrivateStorage(uri, mimeType)
            ?: return AttachmentResult(error = "读取图片失败")
        return AttachmentResult(
            value = ChatAttachmentUi(
                id = UUID.randomUUID().toString(),
                localUri = persistedUri.toString(),
                mimeType = mimeType,
                fileSize = fileSize.coerceAtLeast(0L),
                width = bounds?.outWidth?.takeIf { it > 0 },
                height = bounds?.outHeight?.takeIf { it > 0 }
            )
        )
    }

    suspend fun repairOrphans() {
        val dir = File(context.filesDir, "chat_attachments")
        val orphanFiles = dir.listFiles()?.filter { it.isFile } ?: return
        if (orphanFiles.isEmpty()) return
        val imageMessages = chatRepository.getMessages(sessionId).filter {
            it.role == MessageRole.USER && it.contentType == ContentType.IMAGE && it.status != MessageStatus.ERROR
        }
        if (imageMessages.isEmpty()) return
        val linkedPaths = chatRepository.getAttachmentsByMessageIds(imageMessages.map { it.messageId })
            .mapNotNull { runCatching { Uri.parse(it.localUri).path }.getOrNull() }
        var remaining = orphanFiles.filter { it.absolutePath !in linkedPaths }
        val windowMs = 10 * 60 * 1000L
        for (message in imageMessages) {
            val best = remaining.minByOrNull { kotlin.math.abs(it.lastModified() - message.timestamp) } ?: break
            if (kotlin.math.abs(best.lastModified() - message.timestamp) > windowMs) continue
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeFile(best.absolutePath, bounds)
            chatRepository.saveMessageAttachments(
                listOf(
                    ChatAttachmentEntity(
                        attachmentId = UUID.randomUUID().toString(),
                        messageId = message.messageId,
                        sessionId = sessionId,
                        mimeType = if (best.extension.equals("png", true)) "image/png" else "image/jpeg",
                        localUri = Uri.fromFile(best).toString(),
                        fileSize = best.length(),
                        width = bounds.outWidth.takeIf { it > 0 },
                        height = bounds.outHeight.takeIf { it > 0 }
                    )
                )
            )
            remaining = remaining - best
        }
    }

    private fun copyToPrivateStorage(source: Uri, mimeType: String): Uri? = runCatching {
        val dir = File(context.filesDir, "chat_attachments").apply { if (!exists()) mkdirs() }
        val target = File(dir, "${UUID.randomUUID()}.${if (mimeType == "image/png") "png" else "jpg"}")
        val input = context.contentResolver.openInputStream(source) ?: return null
        input.use { stream -> FileOutputStream(target).use(stream::copyTo) }
        Uri.fromFile(target)
    }.getOrNull()
}
