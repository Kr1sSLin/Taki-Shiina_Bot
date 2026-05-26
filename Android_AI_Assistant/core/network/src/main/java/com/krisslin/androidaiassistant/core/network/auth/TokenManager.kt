package com.krisslin.androidaiassistant.core.network.auth

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKeys
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Token 状态：用于观察当前认证状态
 */
sealed interface TokenState {
    /** 未登录或 token 无效 */
    data object Unauthenticated : TokenState
    /** 已认证，持有有效 token */
    data class Authenticated(val accessToken: String) : TokenState
}

/**
 * TokenManager 使用 EncryptedSharedPreferences 安全存储 token
 * 提供 token 状态 Flow 供 UI 和其他组件观察
 */
@Singleton
class TokenManager @Inject constructor(
    @ApplicationContext private val context: Context
) {
    private val masterKeyAlias = MasterKeys.getOrCreate(MasterKeys.AES256_GCM_SPEC)

    private val prefs: SharedPreferences by lazy {
        EncryptedSharedPreferences.create(
            "auth_prefs",
            masterKeyAlias,
            context,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    private val _tokenState = MutableStateFlow<TokenState>(loadInitialState())
    val tokenState: StateFlow<TokenState> = _tokenState.asStateFlow()

    private fun loadInitialState(): TokenState {
        val accessToken = prefs.getString(KEY_ACCESS_TOKEN, null)
        return if (accessToken.isNullOrBlank()) {
            TokenState.Unauthenticated
        } else {
            TokenState.Authenticated(accessToken)
        }
    }

    /**
     * 获取当前 accessToken，若不存在返回 null
     */
    fun getAccessToken(): String? = prefs.getString(KEY_ACCESS_TOKEN, null)

    /**
     * 获取当前 refreshToken，若不存在返回 null
     */
    fun getRefreshToken(): String? = prefs.getString(KEY_REFRESH_TOKEN, null)

    /**
     * 获取或生成设备ID（用于设备维度鉴权）
     */
    fun getOrCreateDeviceId(): String {
        val existing = prefs.getString(KEY_DEVICE_ID, null)
        if (!existing.isNullOrBlank()) {
            return existing
        }
        val newId = "device_${UUID.randomUUID()}"
        prefs.edit().putString(KEY_DEVICE_ID, newId).apply()
        return newId
    }

    /**
     * 保存 token 对（登录或刷新成功后调用）
     */
    fun saveTokens(accessToken: String, refreshToken: String) {
        prefs.edit()
            .putString(KEY_ACCESS_TOKEN, accessToken)
            .putString(KEY_REFRESH_TOKEN, refreshToken)
            .apply()
        _tokenState.value = TokenState.Authenticated(accessToken)
    }

    /**
     * 清除所有 token（登出或 token 失效时调用）
     */
    fun clearTokens() {
        prefs.edit()
            .remove(KEY_ACCESS_TOKEN)
            .remove(KEY_REFRESH_TOKEN)
            .apply()
        _tokenState.value = TokenState.Unauthenticated
    }

    /**
     * 判断当前是否已认证
     */
    fun isAuthenticated(): Boolean = getAccessToken() != null

    companion object {
        private const val KEY_ACCESS_TOKEN = "access_token"
        private const val KEY_REFRESH_TOKEN = "refresh_token"
        private const val KEY_DEVICE_ID = "device_id"
    }
}
