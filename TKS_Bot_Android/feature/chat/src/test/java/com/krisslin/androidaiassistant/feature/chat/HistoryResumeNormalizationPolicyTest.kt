package com.krisslin.androidaiassistant.feature.chat

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class HistoryResumeNormalizationPolicyTest {
    @Test
    fun `normalizes only when no request or stream is active`() {
        assertTrue(
            HistoryResumeNormalizationPolicy.shouldNormalize(
                hasPendingRequests = false,
                hasStreamingContent = false
            )
        )
        assertFalse(
            HistoryResumeNormalizationPolicy.shouldNormalize(
                hasPendingRequests = true,
                hasStreamingContent = false
            )
        )
        assertFalse(
            HistoryResumeNormalizationPolicy.shouldNormalize(
                hasPendingRequests = false,
                hasStreamingContent = true
            )
        )
        assertFalse(
            HistoryResumeNormalizationPolicy.shouldNormalize(
                hasPendingRequests = true,
                hasStreamingContent = true
            )
        )
    }
}
