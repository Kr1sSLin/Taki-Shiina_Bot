package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.JsonParser
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BotReplySegmentsTest {
    @Test
    fun `segmented display timestamps never advance source time`() {
        val items = TimelineHistoryNormalizer.normalize(JsonParser.parseString(
            """[{"messageId":"reply","role":"bot","content":"one\ntwo\nthree","timestamp":1000}]"""
        ).asJsonArray)
        assertEquals(listOf(1000L, 1001L, 1002L), items.map { it.timestamp })
        assertEquals(listOf(1000L, 1000L, 1000L), items.map { it.sourceTimestamp })
    }

    @Test
    fun `empty content keeps original stable id`() {
        assertEquals(
            listOf(BotReplySegment("reply", "", 10L)),
            BotReplySegments.create("reply", "", 10L)
        )
    }

    @Test
    fun `single line uses indexed stable id`() {
        assertEquals(
            listOf(BotReplySegment("reply_0", "hello", 10L)),
            BotReplySegments.create("reply", " hello ", 10L)
        )
    }

    @Test
    fun `multiline removes blanks and keeps deterministic ids and order`() {
        assertEquals(
            listOf(
                BotReplySegment("reply_0", "first", 10L),
                BotReplySegment("reply_1", "second", 11L)
            ),
            BotReplySegments.create("reply", " first\n\n second ", 10L)
        )
    }

    @Test
    fun `ws http and history produce identical primary keys`() {
        val ws = BotReplySegments.create("same-id", "one\ntwo", 42L)
        val http = BotReplySegments.create("same-id", "one\ntwo", 42L)
        val history = TimelineHistoryNormalizer.normalize(
            JsonParser.parseString(
                """[{"messageId":"same-id","role":"bot","content":"one\ntwo","timestamp":42}]"""
            ).asJsonArray
        )

        assertEquals(ws, http)
        assertEquals(ws.map { it.messageId }, history.map { it.messageId })
        assertEquals(ws.map { it.content }, history.map { it.content })
        assertEquals(ws.map { it.timestamp }, history.map { it.timestamp })
    }

    @Test
    fun `history preserves user id and filters malformed or blank items`() {
        val normalized = TimelineHistoryNormalizer.normalize(
            JsonParser.parseString(
                """[
                    {"messageId":"request-id","role":"user","content":"sent","timestamp":7},
                    {"messageId":"blank","role":"bot","content":"  ","timestamp":8},
                    {"messageId":"missing-time","role":"bot","content":"ignored"}
                ]""".trimIndent()
            ).asJsonArray
        )

        assertEquals(listOf(TimelineHistoryItem("request-id", "user", "sent", 7L)), normalized)
    }

    @Test
    fun `history malformed fields do not discard valid neighboring messages`() {
        val normalized = TimelineHistoryNormalizer.normalize(
            JsonParser.parseString(
                """[
                    {"messageId":"before","role":"user","content":"first","timestamp":1},
                    {"messageId":"bad-content","role":"bot","content":{},"timestamp":2},
                    {"messageId":"bad-time","role":"bot","content":"ignored","timestamp":{}},
                    {"messageId":"bad-number","role":"bot","content":"ignored","timestamp":"oops"},
                    {"messageId":[],"role":"bot","content":"ignored","timestamp":3},
                    null, false, [],
                    {"messageId":"after","role":"bot","content":"last","timestamp":4}
                ]""".trimIndent()
            ).asJsonArray
        )
        assertEquals(
            listOf(
                TimelineHistoryItem("before", "user", "first", 1L),
                TimelineHistoryItem("after_0", "bot", "last", 4L)
            ),
            normalized
        )
    }

    @Test
    fun `delivered tracker suppresses same id fallback`() {
        val tracker = DeliveredReplyTracker()
        assertFalse(tracker.contains("id"))
        tracker.mark("id")
        assertTrue(tracker.contains("id"))
    }
}
