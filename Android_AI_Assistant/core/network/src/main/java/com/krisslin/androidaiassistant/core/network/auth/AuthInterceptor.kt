package com.krisslin.androidaiassistant.core.network.auth

import okhttp3.Interceptor
import okhttp3.Response
import javax.inject.Inject
import javax.inject.Singleton

/**
 * OkHttp 拦截器：自动为请求添加 Authorization header
 * 跳过 auth 相关接口（login/refresh）避免循环
 */
@Singleton
class AuthInterceptor @Inject constructor(
    private val tokenManager: TokenManager
) : Interceptor {

    override fun intercept(chain: Interceptor.Chain): Response {
        val originalRequest = chain.request()
        val url = originalRequest.url.toString()

        // 跳过认证接口
        if (url.contains("/auth/login") || url.contains("/auth/refresh")) {
            return chain.proceed(originalRequest)
        }

        val accessToken = tokenManager.getAccessToken()
        return if (accessToken != null) {
            val newRequest = originalRequest.newBuilder()
                .header("Authorization", "Bearer $accessToken")
                .build()
            chain.proceed(newRequest)
        } else {
            chain.proceed(originalRequest)
        }
    }
}
