package com.krisslin.androidaiassistant

import androidx.lifecycle.ViewModel
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.StateFlow
import javax.inject.Inject

/**
 * 暴露全局认证状态，驱动 AppRoot 在登录页与聊天页之间切换
 */
@HiltViewModel
class MainViewModel @Inject constructor(
    authRepository: AuthRepository
) : ViewModel() {
    val tokenState: StateFlow<TokenState> = authRepository.tokenState
}
