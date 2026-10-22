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

}
