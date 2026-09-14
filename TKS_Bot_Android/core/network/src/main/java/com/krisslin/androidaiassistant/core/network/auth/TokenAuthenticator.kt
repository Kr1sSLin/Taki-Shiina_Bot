package com.krisslin.androidaiassistant.core.network.auth

import kotlinx.coroutines.runBlocking
import okhttp3.Authenticator
import okhttp3.Request
import okhttp3.Response
import okhttp3.Route
import javax.inject.Inject
import javax.inject.Provider
import javax.inject.Singleton

/**
 * OkHttp Authenticator：处理 401 响应，尝试刷新 token 并重试请求
 * 使用 Provider<AuthRepository> 避免循环依赖
 */
@Singleton
class TokenAuthenticator @Inject constructor(
    private val authRepositoryProvider: Provider<AuthRepository>
) : Authenticator {

    override fun authenticate(route: Route?, response: Response): Request? {
        val url = response.request.url.toString()

        // 如果是 refresh 接口返回 401，说明 refreshToken 也失效了，不再重试
        if (url.contains("/auth/refresh")) {
            return null
        }

        // 避免无限循环：检查是否已经重试过
        if (responseCount(response) >= MAX_RETRY_COUNT) {
            return null
        }

        // 同步刷新 token（OkHttp Authenticator 要求同步）
        val newAccessToken = runBlocking {
            authRepositoryProvider.get().refreshToken()
        }

        return if (newAccessToken != null) {
            response.request.newBuilder()
                .header("Authorization", "Bearer $newAccessToken")
                .build()
        } else {
            // 刷新失败，返回 null 让原始请求失败
            null
        }
    }

    private fun responseCount(response: Response): Int {
        var count = 1
        var priorResponse = response.priorResponse
        while (priorResponse != null) {
            count++
            priorResponse = priorResponse.priorResponse
        }
        return count
    }

    companion object {
        private const val MAX_RETRY_COUNT = 2
    }
}
