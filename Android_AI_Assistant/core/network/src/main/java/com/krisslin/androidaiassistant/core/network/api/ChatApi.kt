package com.krisslin.androidaiassistant.core.network.api

import com.google.gson.JsonObject
import retrofit2.http.Body
import retrofit2.http.Header
import retrofit2.http.GET
import retrofit2.http.POST
import retrofit2.http.Query

interface ChatApi {
    @POST("chat")
    suspend fun chat(
        @Header("Authorization") authorization: String? = null,
        @Body request: ChatHttpRequest
    ): JsonObject

    @GET("chat/history")
    suspend fun history(
        @Header("Authorization") authorization: String? = null,
        @Query("since") since: Long = 0,
        @Query("limit") limit: Int = 300
    ): JsonObject
}

data class ChatHttpRequest(
    val requestId: String,
    val message: String,
    val stream: Boolean = false
)

