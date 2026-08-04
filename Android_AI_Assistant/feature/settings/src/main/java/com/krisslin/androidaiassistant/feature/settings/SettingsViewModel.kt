package com.krisslin.androidaiassistant.feature.settings

import android.content.Context
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.JsonObject
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.CityHttpRequest
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

data class SettingsUiState(
    val cityInput: String = "",
    val loading: Boolean = false,
    val message: String? = null,
    val themeMode: ThemeMode = ThemeMode.SYSTEM
)

@HiltViewModel
class SettingsViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val chatApi: ChatApi,
    private val chatRepository: ChatRepository
) : ViewModel() {

    private val themeStore by lazy { ThemePreferenceStore(context) }
    private val _uiState = MutableStateFlow(SettingsUiState())
    val uiState: StateFlow<SettingsUiState> = _uiState.asStateFlow()

    init {
        _uiState.update { it.copy(themeMode = themeStore.themeMode) }
        loadCity()
    }

    fun onCityInputChange(value: String) {
        _uiState.update { it.copy(cityInput = value) }
    }

    fun clearMessage() {
        _uiState.update { it.copy(message = null) }
    }

    fun setThemeMode(mode: ThemeMode) {
        themeStore.updateThemeMode(mode)
        _uiState.update { it.copy(themeMode = mode, message = "主题已切换为：${mode.label()}") }
    }

    fun loadCity() {
        viewModelScope.launch {
            _uiState.update { it.copy(loading = true, message = null) }
            runCatching { chatApi.getCity() }
                .onSuccess { response ->
                    val city = response.dataObject()?.get("city")?.asString
                        ?.substringBefore('#')
                        ?.trim()
                        .orEmpty()
                    _uiState.update { it.copy(cityInput = city, loading = false, message = "已加载城市设置") }
                }
                .onFailure { e ->
                    _uiState.update { it.copy(loading = false, message = "加载失败: ${e.message}") }
                }
        }
    }

    fun saveCity() {
        val city = _uiState.value.cityInput.trim().substringBefore('#').trim()
        if (city.isEmpty()) {
            _uiState.update { it.copy(message = "城市不能为空") }
            return
        }

        viewModelScope.launch {
            _uiState.update { it.copy(loading = true, message = null) }
            runCatching { chatApi.setCity(request = CityHttpRequest(city)) }
                .onSuccess {
                    _uiState.update { current ->
                        current.copy(
                            cityInput = city,
                            loading = false,
                            message = "城市已更新为：$city"
                        )
                    }
                }
                .onFailure { e ->
                    _uiState.update { it.copy(loading = false, message = "更新失败: ${e.message}") }
                }
        }
    }

    fun clearConversation() {
        viewModelScope.launch {
            runCatching { chatRepository.clearSession(DEFAULT_SESSION_ID) }
                .onSuccess { _uiState.update { it.copy(message = "会话记录已清空") } }
                .onFailure { e -> _uiState.update { it.copy(message = "清空失败: ${e.message}") } }
        }
    }

    private fun JsonObject.dataObject(): JsonObject? {
        val data = get("data") ?: return null
        return if (data.isJsonObject) data.asJsonObject else null
    }

    companion object {
        private const val DEFAULT_SESSION_ID = "default_session"
    }
}

fun ThemeMode.label(): String = when (this) {
    ThemeMode.SYSTEM -> "跟随系统"
    ThemeMode.LIGHT -> "日间模式"
    ThemeMode.DARK -> "夜间模式"
}
