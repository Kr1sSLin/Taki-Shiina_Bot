package com.krisslin.androidaiassistant.feature.profile

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

private const val PAGE_SIZE = 20

/** 积分流水行（已把 `reason_code` 映射为中文文案）。 */
data class PointsHistoryItemUi(
    val id: Long,
    val reason: String,
    val changeAmount: Int,
    val balanceAfter: Int,
    val createdAt: Long
)

data class PointsHistoryUiState(
    val loading: Boolean = false,
    val loadingMore: Boolean = false,
    val items: List<PointsHistoryItemUi> = emptyList(),
    val page: Int = 1,
    val hasMore: Boolean = false,
    val error: String? = null
)

/**
 * 积分流水（PRD FR-11）：分页加载，滚动到底部继续取下一页。
 *
 * 数据来源：`GET /api/v1/points/history?page&pageSize=20`。
 */
@HiltViewModel
class PointsHistoryViewModel @Inject constructor(
    private val api: GamificationApi
) : ViewModel() {

    private val _state = MutableStateFlow(PointsHistoryUiState())
    val state: StateFlow<PointsHistoryUiState> = _state.asStateFlow()

    init {
        refresh()
    }

    /** 回到第一页重新加载（也用于错误重试）。 */
    fun refresh() {
        load(page = 1)
    }

    /** 滚动到底部时加载下一页。 */
    fun loadMore() {
        val current = _state.value
        if (current.loading || current.loadingMore || !current.hasMore) return
        load(page = current.page + 1)
    }

    private fun load(page: Int) {
        viewModelScope.launch {
            val isFirstPage = page <= 1
            _state.update {
                it.copy(
                    loading = isFirstPage,
                    loadingMore = !isFirstPage,
                    error = null
                )
            }
            runCatching { api.pointsHistory(page = page, pageSize = PAGE_SIZE) }
                .onSuccess { envelope ->
                    val data = envelope.data
                    if (envelope.code != 0 || data == null) {
                        _state.update {
                            it.copy(
                                loading = false,
                                loadingMore = false,
                                error = envelope.message ?: "流水加载失败"
                            )
                        }
                        return@onSuccess
                    }
                    val mapped = data.items.map { item ->
                        PointsHistoryItemUi(
                            id = item.id,
                            reason = reasonText(item.reasonCode, item.relatedItemId),
                            changeAmount = item.changeAmount,
                            balanceAfter = item.balanceAfter,
                            createdAt = item.createdAt
                        )
                    }
                    _state.update { current ->
                        current.copy(
                            loading = false,
                            loadingMore = false,
                            items = if (isFirstPage) mapped else current.items + mapped,
                            page = data.page.coerceAtLeast(page),
                            hasMore = data.hasMore,
                            error = null
                        )
                    }
                }
                .onFailure { e ->
                    _state.update {
                        it.copy(
                            loading = false,
                            loadingMore = false,
                            error = e.message ?: "网络异常，请稍后重试"
                        )
                    }
                }
        }
    }
}

/** `reason_code` → 中文文案（PRD 5.3 积分规则）。 */
internal fun reasonText(reasonCode: String, relatedItemId: String?): String = when (reasonCode) {
    "DAILY_FIRST_CHAT" -> "每日首次对话"
    "STREAK_3_DAY" -> "连续陪伴 3 天"
    "ANNIVERSARY" -> "纪念日奖励"
    // 流水只下发物品 id，客户端无物品名称接口时直接展示 id，便于对账
    "ITEM_SEND" -> if (relatedItemId.isNullOrBlank()) "送出互动礼物" else "送出 $relatedItemId"
    "ITEM_REFUND" -> "互动失败退回"
    // 仅由测试/运维接口 /api/v1/admin/points/adjust 产生
    "ADMIN_ADJUST" -> if (relatedItemId.isNullOrBlank()) "手动调整（测试）" else "手动调整（测试）"
    else -> if (reasonCode.isBlank()) "积分变动" else reasonCode
}
