package com.krisslin.androidaiassistant.feature.profile

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.network.api.ApiEnvelope
import com.krisslin.androidaiassistant.core.network.api.GamificationApi
import com.krisslin.androidaiassistant.core.network.api.MakeupCardUseRequest
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

private const val HISTORY_PAGE_SIZE = 20

/** 可补签日期候选（`daysAgo` 用于「3 天前」文案）。 */
data class MakeupCardCandidateUi(
    val date: String,
    val daysAgo: Int
)

/** 补签卡库存行（发放/使用历史）。 */
data class MakeupCardHistoryItemUi(
    val id: Long,
    val grantedMonth: String,
    val status: String,
    val usedForDate: String?,
    val createdAt: Long
)

data class MakeupCardUiState(
    val loading: Boolean = false,
    val using: Boolean = false,
    val available: Int = 0,
    val used: Int = 0,
    val totalGranted: Int = 0,
    val maxAvailable: Int = 12,
    val monthlyGrant: Int = 1,
    val lastGrantedMonth: String? = null,
    val currentMonthGranted: Boolean = false,
    val atLimit: Boolean = false,
    val candidates: List<MakeupCardCandidateUi> = emptyList(),
    val history: List<MakeupCardHistoryItemUi> = emptyList(),
    val error: String? = null,
    /** 一次性提示（补签结果），由 UI 消费后调用 [MakeupCardViewModel.consumeMessage]。 */
    val message: String? = null
)

/**
 * 补签卡（PRD FR-19 / FR-20 / FR-21 / FR-22）。
 *
 * 关键约束：**补签只能由用户主动点击触发，系统绝不自动使用**（FR-20），
 * 因此 [useCard] 只由 UI 的确认弹窗回调调用。
 */
@HiltViewModel
class MakeupCardViewModel @Inject constructor(
    private val api: GamificationApi
) : ViewModel() {

    private val _state = MutableStateFlow(MakeupCardUiState())
    val state: StateFlow<MakeupCardUiState> = _state.asStateFlow()

    init {
        refresh()
    }

    fun consumeMessage() {
        _state.update { it.copy(message = null) }
    }

    /** 刷新概要 + 候选日期 + 历史记录。 */
    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(loading = true, error = null) }

            val summaryEnvelope = runCatching { api.makeupCardSummary() }.getOrNull()
            val summary = summaryEnvelope.dataOrNull()
            val candidatesEnvelope = runCatching { api.makeupCardCandidates() }.getOrNull()
            val candidates = candidatesEnvelope.dataOrNull()
            val historyEnvelope = runCatching {
                api.makeupCardHistory(page = 1, pageSize = HISTORY_PAGE_SIZE)
            }.getOrNull()
            val history = historyEnvelope.dataOrNull()

            if (summary == null && candidates == null && history == null) {
                _state.update {
                    it.copy(
                        loading = false,
                        error = summaryEnvelope?.message
                            ?: candidatesEnvelope?.message
                            ?: historyEnvelope?.message
                            ?: "加载失败，请稍后重试"
                    )
                }
                return@launch
            }

            _state.update { current ->
                current.copy(
                    loading = false,
                    error = null,
                    available = summary?.available ?: current.available,
                    used = summary?.used ?: current.used,
                    totalGranted = summary?.totalGranted ?: current.totalGranted,
                    maxAvailable = summary?.maxAvailable ?: current.maxAvailable,
                    monthlyGrant = summary?.monthlyGrant ?: current.monthlyGrant,
                    lastGrantedMonth = summary?.lastGrantedMonth ?: current.lastGrantedMonth,
                    currentMonthGranted = summary?.currentMonthGranted ?: current.currentMonthGranted,
                    atLimit = summary?.atLimit ?: current.atLimit,
                    candidates = candidates?.items
                        ?.map { MakeupCardCandidateUi(date = it.date, daysAgo = it.daysAgo) }
                        ?: current.candidates,
                    history = history?.items
                        ?.map { card ->
                            MakeupCardHistoryItemUi(
                                id = card.id,
                                grantedMonth = card.grantedMonth,
                                status = card.status,
                                usedForDate = card.usedForDate,
                                createdAt = card.createdAt
                            )
                        }
                        ?: current.history
                )
            }
        }
    }

    /**
     * 使用一张补签卡为 [targetDate] 补签（仅由用户确认后调用）。
     *
     * 成功后刷新候选日期 + 概要 + 历史，并通过 [MakeupCardUiState.message] 提示结果等级。
     */
    fun useCard(targetDate: String) {
        if (_state.value.using) return
        viewModelScope.launch {
            _state.update { it.copy(using = true, error = null) }
            runCatching { api.makeupCardUse(request = MakeupCardUseRequest(targetDate = targetDate)) }
                .onSuccess { envelope ->
                    val data = envelope.data
                    if (envelope.code == 0 && data != null && data.success) {
                        _state.update {
                            it.copy(
                                using = false,
                                available = data.availableCards,
                                message = makeupSuccessText(data.level?.changeType, data.level?.levelCode, data.level?.levelName),
                                error = null
                            )
                        }
                        // 补签会改变连续天数与等级，需重新拉取概要/候选/历史
                        refresh()
                    } else {
                        _state.update {
                            it.copy(
                                using = false,
                                error = makeupCardErrorText(
                                    code = envelope.code,
                                    errorCode = data?.errorCode,
                                    message = data?.message ?: envelope.message
                                )
                            )
                        }
                    }
                }
                .onFailure { e ->
                    _state.update {
                        it.copy(using = false, error = e.message ?: "网络异常，请稍后重试")
                    }
                }
        }
    }
}

/** 仅当 code == 0 时取 data；网络异常（null）与业务失败一律视为无数据。 */
private fun <T> ApiEnvelope<T>?.dataOrNull(): T? =
    if (this != null && this.code == 0) this.data else null

/** 补签成功文案：RESTORE 表示回溯挽回，其余按当前等级展示；NONE 态只说“补签成功”（EDGE-7）。 */
internal fun makeupSuccessText(changeType: String?, levelCode: String?, levelName: String?): String {
    val name = levelName.orEmpty().trim()
    return when {
        levelCode.isNullOrBlank() || levelCode == "NONE" || name.isEmpty() -> "补签成功"
        changeType == "RESTORE" -> "补签成功，等级已恢复为 $name"
        else -> "补签成功，当前为 $name"
    }
}

/** 业务错误码 → 中文提示（契约 §1.1）。 */
internal fun makeupCardErrorText(code: Int, errorCode: String?, message: String?): String {
    val key = if (code != 0) code.toString() else errorCode.orEmpty()
    return when (key) {
        "40205" -> "补签卡不足"
        "40206" -> "该日期已有有效对话记录，无需补签"
        "40207" -> "不能为未来日期补签或日期格式错误"
        else -> message?.takeIf { it.isNotBlank() } ?: "补签失败，请稍后重试"
    }
}
