package com.krisslin.androidaiassistant.core.network.api

import retrofit2.http.Body
import retrofit2.http.POST

interface AuthApi {
    @POST("auth/login")
    suspend fun login(@Body request: LoginRequest): TokenResponse

    @POST("auth/refresh")
    suspend fun refreshToken(@Body request: RefreshTokenRequest): TokenResponse
}

data class LoginRequest(val username: String, val password: String)

data class RefreshTokenRequest(val refreshToken: String)

data class TokenResponse(val accessToken: String, val refreshToken: String)