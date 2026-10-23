package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.JsonParser
import com.krisslin.androidaiassistant.core.database.repository.ContentType
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

class HistoryImageRestoreTest {
    @Test
    fun `split bubbles retain parent authority identity`() {
        val items = JsonParser.parseString("""[{"messageId":"parent","role":"bot","content":"one\ntwo","timestamp":42,"historySource":"sql"}]""").asJsonArray
        val normalized = TimelineHistoryNormalizer.normalize(items)
        assertEquals(listOf("parent", "parent"), normalized.map { it.sourceMessageId })
        assertEquals(listOf("sql", "sql"), normalized.map { it.historySource })
    }

    @Test
    fun `timeline retains distinct download request id and original image count`() {
        val root = generateSequence(File(System.getProperty("user.dir")!!)) { it.parentFile }
            .first { File(it, "design/foundation-protocol-samples.json").exists() }
        val page = JsonParser.parseString(File(root, "design/foundation-protocol-samples.json").readText())
            .asJsonObject.getAsJsonObject("historySync").getAsJsonObject("pageData")
        assertEquals(1, page.get("historySyncVersion").asInt)
        assertEquals("opaque-synthetic-checkpoint", page.get("nextCursor").asString)
        val items = page.getAsJsonArray("items")

        val normalized = TimelineHistoryNormalizer.normalize(items)

        assertEquals(1, normalized.size)
        assertEquals("client-message", normalized.single().messageId)
        assertEquals("server-request", normalized.single().requestId)
        assertEquals(2, normalized.single().imageCount)
        assertEquals(42L, normalized.single().sourceTimestamp)
        assertEquals("sql", normalized.single().historySource)
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
