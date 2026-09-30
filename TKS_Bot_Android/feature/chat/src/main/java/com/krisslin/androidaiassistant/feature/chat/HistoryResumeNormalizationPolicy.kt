package com.krisslin.androidaiassistant.feature.chat

/** Decides whether persisted streaming rows are stale and safe to normalize. */
internal object HistoryResumeNormalizationPolicy {
    fun shouldNormalize(
        hasPendingRequests: Boolean,
        hasStreamingContent: Boolean
    ): Boolean = !hasPendingRequests && !hasStreamingContent
}
