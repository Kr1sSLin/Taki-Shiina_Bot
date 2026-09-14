package com.krisslin.androidaiassistant.feature.auth

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.io.IOException
import javax.inject.Inject

data class AuthUiState(
    val username: String = "",
    val password: String = "",
    val loading: Boolean = false,
    val error: String? = null
)

@HiltViewModel
class AuthViewModel @Inject constructor(
    private val authRepository: AuthRepository
) : ViewModel() {

    private val _uiState = MutableStateFlow(AuthUiState())
    val uiState: StateFlow<AuthUiState> = _uiState.asStateFlow()

    fun onUsernameChange(value: String) {
        _uiState.update { it.copy(username = value, error = null) }
    }

    fun onPasswordChange(value: String) {
        _uiState.update { it.copy(password = value, error = null) }
    }

    fun login() {
        val current = _uiState.value
        if (current.loading) return
        if (current.username.isBlank() || current.password.isBlank()) {
            _uiState.update { it.copy(error = "请输入用户名和密码") }
            return
        }
        _uiState.update { it.copy(loading = true, error = null) }
        viewModelScope.launch {
            val result = authRepository.login(current.username.trim(), current.password)
            result.onSuccess {
                // tokenState 转为 Authenticated，由 AppRoot 自动切换到聊天页
                _uiState.update { it.copy(loading = false) }
            }.onFailure { e ->
                _uiState.update { it.copy(loading = false, error = mapLoginError(e)) }
            }
        }
    }

    private fun mapLoginError(e: Throwable): String {
        return when {
            e is HttpException && e.code() == 401 -> "用户名或密码错误"
            e is HttpException && e.code() == 403 -> "设备数超限或设备未授权，请清理后重试"
            e is HttpException -> "登录失败（HTTP ${e.code()}），请重试"
            e is IOException -> "无法连接服务器，请检查网络"
            else -> "登录失败，请重试"
        }
    }
}
