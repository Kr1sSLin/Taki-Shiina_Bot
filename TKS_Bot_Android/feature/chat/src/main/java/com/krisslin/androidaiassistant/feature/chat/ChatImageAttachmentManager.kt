package com.krisslin.androidaiassistant.feature.chat

import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.ContentType
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.UUID

internal data class OutgoingImage(val ui: ChatAttachmentUi, val dataBase64: String)
internal data class OutgoingPayload(val text: String, val images: List<OutgoingImage>)
internal data class AttachmentResult<T>(val value: T? = null, val error: String? = null)

/** Owns image validation, private-file persistence, encoding, and legacy attachment repair. */
internal class ChatImageAttachmentManager(
    private val context: Context,
    private val chatRepository: ChatRepository,
    private val sessionId: String,
    private val chatApi: ChatApi
) {
    companion object {
        private const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
        const val MAX_IMAGE_TIPS = "图片超过 20MB 限制"
    }

    private val historyRestoreMutex = Mutex()

    /** Queue absent originals in Room, then download them from the authenticated endpoint. */
    suspend fun restoreHistoryImages(requestId: String, messageId: String, imageCount: Int) {
        if (requestId.isBlank() || messageId.isBlank() || imageCount !in 1..3) return
        historyRestoreMutex.withLock {
            val existing = chatRepository.getAttachmentsByMessageId(messageId)
            val localCount = existing.count { !it.attachmentId.startsWith("remote_") }
            val existingIds = existing.map { it.attachmentId }.toSet()
            val remoteIndices = (0 until imageCount).filter { historyAttachmentId(messageId, it) in existingIds }.toSet()
            val missing = HistoryImageRestorePolicy.missingIndices(imageCount, localCount == imageCount, remoteIndices).map { index ->
                ChatAttachmentEntity(
                    attachmentId = historyAttachmentId(messageId, index),
                    messageId = messageId,
                    sessionId = sessionId,
                    mimeType = "",
                    localUri = "pending-history:///${Uri.encode(requestId)}/$index",
                    fileSize = 0,
                    uploadState = "pending_history",
                    timestamp = System.currentTimeMillis()
                )
            }
            chatRepository.saveMessageAttachments(missing)
            for (pending in existing + missing) {
                if (pending.uploadState == "pending_history") tryDownloadHistoryImage(pending)
            }
        }
    }

    /** Retry persisted misses even after the incremental timeline cursor has passed them. */
    suspend fun retryPendingHistoryImages() {
        historyRestoreMutex.withLock {
            for (pending in chatRepository.getPendingHistoryAttachments(sessionId)) {
                tryDownloadHistoryImage(pending)
            }
        }
    }

    private suspend fun tryDownloadHistoryImage(pending: ChatAttachmentEntity) {
        runCatching {
            val segments = Uri.parse(pending.localUri).pathSegments
            if (segments.size != 2) throw IOException("Invalid pending image reference")
            val requestId = segments[0]
            val index = segments[1].toIntOrNull()?.takeIf { it in 0..2 }
                ?: throw IOException("Invalid image index")
            val response = chatApi.historyImage(requestId, index)
            if (!response.isSuccessful) {
                response.errorBody()?.close()
                throw IOException("Image download returned HTTP ${response.code()}")
            }
            val body = response.body() ?: throw IOException("Empty image response")
            val (mimeType, bytes) = body.use {
                val mime = it.contentType()?.let { type -> "${type.type}/${type.subtype}" }
                    ?: throw IOException("Missing image MIME")
                if (mime != "image/jpeg" && mime != "image/png") throw IOException("Unsupported image MIME")
                if (it.contentLength() > MAX_IMAGE_BYTES) throw IOException("Image exceeds 20MB")
                val output = ByteArrayOutputStream()
                it.byteStream().use { stream ->
                    val buffer = ByteArray(8192)
                    while (true) {
                        val read = stream.read(buffer)
                        if (read < 0) break
                        if (output.size() + read > MAX_IMAGE_BYTES) throw IOException("Image exceeds 20MB")
                        output.write(buffer, 0, read)
                    }
                }
                mime to output.toByteArray()
            }
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outMimeType != mimeType) {
                throw IOException("Invalid image payload")
            }
            val dir = File(context.filesDir, "chat_attachments/history").apply { mkdirs() }
            val extension = if (mimeType == "image/png") "png" else "jpg"
            val target = File(dir, "history_${java.util.UUID.randomUUID()}.$extension")
            val temp = File.createTempFile("history_", ".$extension", dir)
            try {
                FileOutputStream(temp).use { output ->
                    output.write(bytes)
                    output.fd.sync()
                }
                try {
                    Files.move(temp.toPath(), target.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                } catch (_: AtomicMoveNotSupportedException) {
                    Files.move(temp.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING)
                }
            } finally {
                temp.delete()
            }
            chatRepository.saveMessageAttachments(
                listOf(
                    pending.copy(
                        mimeType = mimeType,
                        localUri = Uri.fromFile(target).toString(),
                        fileSize = bytes.size.toLong(),
                        width = bounds.outWidth,
                        height = bounds.outHeight,
                        uploadState = "downloaded"
                    )
                )
            )
        }.onFailure { error ->
            if (error is CancellationException) throw error
            Log.w("ChatImageAttachments", "history image download failed")
        }
    }

    private fun historyAttachmentId(messageId: String, index: Int): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest("$messageId:$index".toByteArray(Charsets.UTF_8))
        return "remote_${digest.joinToString("") { "%02x".format(it) }}"
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
