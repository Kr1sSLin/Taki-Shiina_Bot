package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.JsonParser
import com.krisslin.androidaiassistant.core.database.repository.ContentType
import org.junit.Assert.assertEquals
import org.junit.Test

class HistoryImageRestoreTest {
    @Test
    fun `timeline retains distinct download request id and original image count`() {
        val items = JsonParser.parseString(
            """[{"messageId":"client-message","requestId":"server-request","role":"user","content":"[图片]","timestamp":42,"imageCount":2}]"""
        ).asJsonArray

        val normalized = TimelineHistoryNormalizer.normalize(items)

        assertEquals(1, normalized.size)
        assertEquals("client-message", normalized.single().messageId)
        assertEquals("server-request", normalized.single().requestId)
        assertEquals(2, normalized.single().imageCount)
        assertEquals(42L, normalized.single().sourceTimestamp)
    }

    @Test
    fun `image only history with empty text remains visible after original downloads`() {
        val items = JsonParser.parseString(
            """[{"messageId":"image-id","requestId":"r","role":"user","content":"","timestamp":7,"imageCount":1}]"""
        ).asJsonArray

        val item = TimelineHistoryNormalizer.normalize(items).single()
        val type = HistoryImageRestorePolicy.contentType(item.content, item.imageCount)

        assertEquals("[图片]", item.content)
        assertEquals(ContentType.IMAGE, type)
        assertEquals("[图片]", HistoryImageRestorePolicy.displayContent(item.content, type, hasAttachments = false))
        assertEquals("", HistoryImageRestorePolicy.displayContent(item.content, type, hasAttachments = true))
        assertEquals(listOf(0), HistoryImageRestorePolicy.missingIndices(1, false, emptySet()))
        assertEquals(listOf(1, 2), HistoryImageRestorePolicy.missingIndices(3, false, setOf(0)))
        assertEquals(emptyList<Int>(), HistoryImageRestorePolicy.missingIndices(3, true, emptySet()))
    }

    @Test
    fun `real caption stays visible and invalid image count cannot allocate downloads`() {
        assertEquals(ContentType.MIXED, HistoryImageRestorePolicy.contentType("我的猫", 2))
        assertEquals("我的猫", HistoryImageRestorePolicy.displayContent("我的猫", ContentType.MIXED, true))
        val items = JsonParser.parseString(
            """[{"messageId":"x","role":"user","content":"","timestamp":1,"imageCount":4294967298}]"""
        ).asJsonArray
        assertEquals(0, TimelineHistoryNormalizer.normalize(items).size)
    }
}
