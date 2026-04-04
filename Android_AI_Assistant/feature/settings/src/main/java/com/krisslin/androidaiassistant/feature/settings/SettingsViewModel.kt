package com.krisslin.androidaiassistant.feature.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.JsonObject
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.CityHttpRequest
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

data class SettingsUiState(
    val cityInput: String = "",
    val loading: Boolean = false,
    val message: String? = null
)

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val chatApi: ChatApi
) : ViewModel() {

    private val _uiState = MutableStateFlow(SettingsUiState())
    val uiState: StateFlow<SettingsUiState> = _uiState.asStateFlow()

    init {
        loadCity()
    }

    fun onCityInputChange(value: String) {
        _uiState.update { it.copy(cityInput = value) }
    }

    fun clearMessage() {
        _uiState.update { it.copy(message = null) }
    }

    fun loadCity() {
        viewModelScope.launch {
            _uiState.update { it.copy(loading = true, message = null) }
            runCatching { chatApi.getCity() }
                .onSuccess { response ->
                    val city = response.dataObject()?.get("city")?.asString ?: ""
                    _uiState.update { it.copy(cityInput = city, loading = false, message = "已加载城市设置") }
                }
                .onFailure { e ->
                    _uiState.update { it.copy(loading = false, message = "加载失败: ${e.message}") }
                }
        }
    }

    fun saveCity() {
        val city = _uiState.value.cityInput.trim()
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

    private fun JsonObject.dataObject(): JsonObject? {
        val data = get("data") ?: return null
        return if (data.isJsonObject) data.asJsonObject else null
    }
}
