package com.krisslin.androidaiassistant.feature.interaction

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.network.api.GamificationApi
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

/**
 * 互动菜单（PRD FR-1 / FR-4 / FR-5）。
 *
 * 数据来源：`GET /api/v1/interaction/items`，服务端已按 `sortOrder` 排序且下发 `affordable`。
 * 鉴权由 [com.krisslin.androidaiassistant.core.network.auth.AuthInterceptor] 统一注入，无需手动传 header。
 */
@HiltViewModel
class InteractionMenuViewModel @Inject constructor(
    private val api: GamificationApi
) : ViewModel() {

    private val _state = MutableStateFlow(InteractionMenuUiState())
    val state: StateFlow<InteractionMenuUiState> = _state.asStateFlow()

    init {
        refresh()
    }

    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(loading = true, error = null) }
            runCatching { api.interactionItems() }
                .onSuccess { envelope ->
                    if (envelope.code != 0) {
                        _state.update {
                            it.copy(loading = false, error = envelope.message ?: "互动菜单加载失败")
                        }
                        return@onSuccess
                    }
                    val data = envelope.data
                    if (data == null) {
                        _state.update { it.copy(loading = false, error = "互动菜单数据为空") }
                        return@onSuccess
                    }
                    _state.update {
                        it.copy(
                            loading = false,
                            balance = data.balance,
                            // FR-5：保持服务端 sortOrder 顺序的简单平铺列表
                            items = data.items
                                .sortedBy { item -> item.sortOrder }
                                .map { item ->
                                    InteractionMenuItemUi(
                                        id = item.id,
                                        name = item.name,
                                        icon = item.icon,
                                        iconUrl = item.iconUrl,
                                        costPoints = item.costPoints,
                                        affordable = item.affordable
                                    )
                                },
                            error = null
                        )
                    }
                }
                .onFailure { e ->
                    _state.update {
                        it.copy(loading = false, error = e.message ?: "网络异常，请稍后重试")
                    }
                }
        }
    }
}
