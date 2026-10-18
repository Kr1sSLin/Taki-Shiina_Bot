package com.krisslin.androidaiassistant.core.network.api

import com.google.gson.JsonObject
import okhttp3.ResponseBody
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.Header
import retrofit2.http.GET
import retrofit2.http.Path
import retrofit2.http.POST
import retrofit2.http.PUT
import retrofit2.http.PATCH
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

    /** The authenticated, original image bytes for a reliable user message. */
    @GET("chat/images/{requestId}/{index}")
    suspend fun historyImage(
        @Path("requestId") requestId: String,
        @Path("index") index: Int
    ): Response<ResponseBody>

    @GET("memory/facts")
    suspend fun memoryFacts(
        @Header("Authorization") authorization: String? = null,
        @Query("cursor") cursor: Long = 0,
        @Query("limit") limit: Int = 200,
        @Query("userId") userId: String = "default-user"
    ): JsonObject

    @GET("memory/facts/{factId}")
    suspend fun memoryFactDetail(@Path("factId") factId: String): JsonObject

    @PATCH("memory/facts/{factId}/permission")
    suspend fun setMemoryPermission(
        @Path("factId") factId: String,
        @Body request: MemoryPermissionHttpRequest
    ): JsonObject

    @PATCH("memory/facts/{factId}/correction")
    suspend fun correctMemoryFact(
        @Path("factId") factId: String,
        @Body request: MemoryCorrectionHttpRequest
    ): JsonObject

    @GET("settings/proactive")
    suspend fun getProactiveSettings(): JsonObject

    @PUT("settings/proactive")
    suspend fun setProactiveSettings(@Body request: ProactiveSettingsHttpRequest): JsonObject

    @GET("settings/city")
    suspend fun getCity(
        @Header("Authorization") authorization: String? = null
    ): JsonObject

    @PUT("settings/city")
    suspend fun setCity(
        @Header("Authorization") authorization: String? = null,
        @Body request: CityHttpRequest
    ): JsonObject
}

data class ChatHttpRequest(
    val requestId: String,
    val message: String,
    val stream: Boolean = false
)

data class CityHttpRequest(
    val city: String
)

data class MemoryPermissionHttpRequest(
    val enabled: Boolean,
    val expectedRevision: Long,
    val operationId: String
)

data class MemoryCorrectionHttpRequest(
    val content: String,
    val expectedRevision: Long,
    val operationId: String
)

data class ProactiveSettingsHttpRequest(val enabled: Boolean)

