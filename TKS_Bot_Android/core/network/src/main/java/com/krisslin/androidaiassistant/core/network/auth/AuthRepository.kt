package com.krisslin.androidaiassistant.core.network.auth

import com.krisslin.androidaiassistant.core.network.api.AuthApi
import com.krisslin.androidaiassistant.core.network.api.LoginRequest
import com.krisslin.androidaiassistant.core.network.api.RefreshTokenRequest
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.inject.Inject
import javax.inject.Singleton

/**
 * AuthRepository 封装认证相关逻辑
 * - 登录、登出
 * - Token 刷新（带并发保护）
 */
@Singleton
class AuthRepository @Inject constructor(
    private val authApi: AuthApi,
    private val tokenManager: TokenManager
) {
    private val refreshMutex = Mutex()

    /** 暴露 token 状态供外部观察 */
    val tokenState: StateFlow<TokenState> = tokenManager.tokenState

    /**
     * 登录
     * @return true 表示登录成功
     */
    suspend fun login(username: String, password: String): Result<Unit> {
        return runCatching {
            val deviceId = tokenManager.getOrCreateDeviceId()
            val response = authApi.login(LoginRequest(username, password, deviceId))
            tokenManager.saveTokens(response.accessToken, response.refreshToken)
        }
    }

    /**
     * 登出，清除本地 token
     */
    fun logout() {
        tokenManager.clearTokens()
    }

    /**
     * 刷新 token
     * 使用 Mutex 保护，避免并发刷新
     * @return 新的 accessToken，失败时返回 null
     */
    suspend fun refreshToken(): String? = refreshMutex.withLock {
        val currentRefreshToken = tokenManager.getRefreshToken() ?: return@withLock null
        return@withLock try {
            val response = authApi.refreshToken(RefreshTokenRequest(currentRefreshToken))
            tokenManager.saveTokens(response.accessToken, response.refreshToken)
            response.accessToken
        } catch (e: Exception) {
            // 刷新失败，清除 token 让用户重新登录
            tokenManager.clearTokens()
            null
        }
    }

    /**
     * 获取当前 accessToken
     */
    fun getAccessToken(): String? = tokenManager.getAccessToken()

    /**
     * 判断是否已登录
     */
    fun isLoggedIn(): Boolean = tokenManager.isAuthenticated()
}
