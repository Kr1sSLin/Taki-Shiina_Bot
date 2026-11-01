package com.krisslin.androidaiassistant.feature.chat

import com.krisslin.androidaiassistant.core.database.repository.ContentType

/** Pure display and backfill rules shared by history sync and live echoes. */
internal object HistoryImageRestorePolicy {
    private val imageOnlyPlaceholder = Regex("\\[图片(?:×[1-3])?\\]")

    fun contentType(content: String, imageCount: Int): String = when {
        imageCount == 0 -> ContentType.TEXT
        content.isBlank() || imageOnlyPlaceholder.matches(content) -> ContentType.IMAGE
        else -> ContentType.MIXED
    }

    fun displayContent(content: String, contentType: String, hasAttachments: Boolean): String =
        if (contentType == ContentType.IMAGE && hasAttachments && imageOnlyPlaceholder.matches(content)) "" else content

    fun missingIndices(
        imageCount: Int,
        completeLocalSet: Boolean,
        presentRemoteIndices: Set<Int>
    ): List<Int> = if (completeLocalSet && presentRemoteIndices.isEmpty()) emptyList() else
        (0 until imageCount.coerceIn(0, 3)).filter { it !in presentRemoteIndices }
}
