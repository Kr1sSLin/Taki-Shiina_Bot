package com.krisslin.androidaiassistant.feature.chat

import com.google.gson.Gson
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser
import org.junit.Assert.assertEquals
import org.junit.Test
import com.google.gson.JsonParser
import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull

class BotErrorTimestampTest {
    private val parser = IncomingMessageParser(Gson())

    @Test
    fun `shared old and terminal frames preserve identity and optional recovery`() {
        val root = generateSequence(File(System.getProperty("user.dir"))) { it.parentFile }
            .first { File(it, "design/foundation-protocol-samples.json").exists() }
        val samples = JsonParser.parseString(File(root, "design/foundation-protocol-samples.json").readText()).asJsonObject
        val recovery = samples.getAsJsonObject("recovery")
        val old = parser.parse(recovery.get("legacyError").toString()) as IncomingMessage.BotError
        assertNull(old.payload.recoveryAction)
        val terminal = parser.parse(recovery.get("terminalError").toString()) as IncomingMessage.BotError
        assertFalse(terminal.payload.retryable!!)
        assertEquals("resend_new_request", terminal.payload.recoveryAction)
        assertEquals(listOf("sample-text", "sample-gift"), terminal.payload.requestIds)
        val sources = recovery.getAsJsonObject("historyData").getAsJsonArray("operations")[0].asJsonObject.getAsJsonArray("sources")
        assertEquals(listOf("保留的文字", "☕ 咖啡"), TimelineHistoryNormalizer.normalize(sources).map { it.content })
        assertEquals("poll_status", recovery.getAsJsonObject("processing").get("recoveryAction").asString)
    }

    @Test
    fun `input budget counts codepoints and preserves recovery decisions`() {
        assertFalse(chatInputTooLong("😀".repeat(8000)))
        org.junit.Assert.assertTrue(chatInputTooLong("😀".repeat(8001)))
        assertFalse(chatInputTooLong("e\u0301".repeat(4000)))
        org.junit.Assert.assertTrue(chatInputTooLong("e\u0301".repeat(4001)))
        assertEquals("EDIT_INPUT", chatRecoveryCode("edit_input", "INPUT_TOO_LONG"))
        assertEquals("POLL_STATUS", chatRecoveryCode("poll_status", "CHAT_RETRY"))
        assertEquals("CHAT_RETRY", chatRecoveryCode(null, "CHAT_RETRY"))
        assertEquals("RETRY_SAME_REQUEST", chatRecoveryCode("retry_same_request", "CHAT_RETRY"))
        val root = generateSequence(File(System.getProperty("user.dir"))) { it.parentFile }
            .first { File(it, "design/foundation-protocol-samples.json").exists() }
        val recovery = JsonParser.parseString(File(root, "design/foundation-protocol-samples.json").readText()).asJsonObject.getAsJsonObject("recovery")
        val frame = parser.parse(recovery.get("inputTooLong").toString()) as IncomingMessage.BotError
        assertEquals("edit_input", frame.payload.recoveryAction)
        assertFalse(frame.payload.retryable!!)
    }

}
