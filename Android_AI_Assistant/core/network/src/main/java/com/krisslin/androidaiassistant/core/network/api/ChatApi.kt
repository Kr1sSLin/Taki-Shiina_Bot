package com.krisslin.androidaiassistant.core.network.api

import com.google.gson.JsonObject
import retrofit2.http.Body
import retrofit2.http.Header
import retrofit2.http.POST

interface ChatApi {
    @POST("chat")
    suspend fun chat(
        @Header("Authorization") authorization: String? = null,
        @Body request: ChatHttpRequest
    ): JsonObject
}

data class ChatHttpRequest(
    val requestId: String,
    val message: String,
    val stream: Boolean = false
)

