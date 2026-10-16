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
        @Query("since") since: Long = 0,
        @Query("limit") limit: Int = 200,
        @Query("userId") userId: String = "default-user"
    ): JsonObject

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

