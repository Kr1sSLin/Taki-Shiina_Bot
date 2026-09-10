package com.krisslin.androidaiassistant.feature.profile

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.database.entity.UserProgressEntity
import com.krisslin.androidaiassistant.core.database.repository.UserProgressRepository
import com.krisslin.androidaiassistant.core.network.api.GamificationApi
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

/** 等级说明表的一行，来自 `GET /api/v1/level/config`（后台可配置，无需发版）。 */
data class LevelThresholdUi(
    val levelCode: String,
    val levelName: String,
    val thresholdDays: Int,
    val reached: Boolean
)

/**
 * 「我的陪伴」页状态（PRD FR-14 / FR-15 / FR-17 / FR-19）。
 *
 * 说明：`levelCode == "NONE"` 是断签回落后的默认态，与“从未升级过的新用户”共用同一套展示，
 * 不设专属文案（EDGE-7），文案交由 UI 层统一处理。
 */
data class ProfileUiState(
    val loading: Boolean = false,
    val balance: Int = 0,
    val levelCode: String = "NONE",
    val levelName: String = "",
    val continuousDays: Int = 0,
    val nextLevelName: String? = null,
    val nextLevelThresholdDays: Int? = null,
    val daysToNextLevel: Int? = null,
    val availableMakeupCards: Int = 0,
    val makeupCardMax: Int = 12,
    val breakGapDays: Int = 5,
    val levelTable: List<LevelThresholdUi> = emptyList(),
    val error: String? = null
)

/**
 * 首屏聚合：`points/overview`（余额 + 等级 + 补签卡）与 `level/config`（阈值表）。
 *
 * 数据优先级：**后端为唯一权威来源**；[UserProgressRepository] 的缓存行只在远端数据到达前
 * 用于离线展示（首屏/无网络），绝不反向覆盖远端结果。首屏拉取由 `ProfileRoute` 的
 * `LaunchedEffect` 触发，避免与 init 重复请求。
 */
@HiltViewModel
class ProfileViewModel @Inject constructor(
    private val api: GamificationApi,
    private val userProgressRepository: UserProgressRepository
) : ViewModel() {

    private val _state = MutableStateFlow(ProfileUiState())
    val state: StateFlow<ProfileUiState> = _state.asStateFlow()

    /** 是否已拿到远端数据：一旦为 true，缓存 flow 不再回灌 UI。 */
    private var remoteLoaded = false

    init {
        observeCache()
    }

    /** 订阅离线缓存（仅用于远端数据到达前的展示）。 */
    private fun observeCache() {
        viewModelScope.launch {
            userProgressRepository.observe(UserProgressRepository.DEFAULT_USER_ID).collect { cached ->
                if (cached == null || remoteLoaded) return@collect
                _state.update { current ->
                    current.copy(
                        balance = cached.balance,
                        levelCode = cached.levelCode,
                        levelName = cached.levelName,
                        continuousDays = cached.continuousDays,
                        nextLevelName = cached.nextLevelName,
                        nextLevelThresholdDays = cached.nextLevelThresholdDays,
                        daysToNextLevel = cached.daysToNextLevel,
                        availableMakeupCards = cached.availableMakeupCards
                    )
                }
            }
        }
    }

    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(loading = true, error = null) }

            val envelope = runCatching { api.pointsOverview() }
                .getOrElse { e ->
                    _state.update {
                        it.copy(loading = false, error = e.message ?: "网络异常，请稍后重试")
                    }
                    return@launch
                }
            // 注意：ApiEnvelope 位于另一模块，属性无法智能转换，先取到本地变量再判空
            val overview = envelope.data
            if (envelope.code != 0 || overview == null) {
                _state.update {
                    it.copy(loading = false, error = envelope.message ?: "加载失败")
                }
                return@launch
            }

            val level = overview.level
            val card = overview.makeupCard

            // 阈值表拉取失败不阻塞主卡片，沿用上一次的等级说明
            val config = runCatching { api.levelConfig() }
                .getOrNull()
                ?.takeIf { it.code == 0 }
                ?.data

            val levelTable = config?.levels
                ?.sortedBy { it.sortOrder }
                ?.map { item ->
                    LevelThresholdUi(
                        levelCode = item.levelCode,
                        levelName = item.levelName,
                        thresholdDays = item.thresholdDays,
                        reached = level.continuousDays >= item.thresholdDays
                    )
                }
                ?.takeIf { it.isNotEmpty() }
                ?: _state.value.levelTable

            remoteLoaded = true
            _state.update { current ->
                current.copy(
                    loading = false,
                    balance = overview.balance,
                    levelCode = level.levelCode,
                    levelName = level.levelName,
                    continuousDays = level.continuousDays,
                    nextLevelName = level.nextLevelName,
                    nextLevelThresholdDays = level.nextLevelThresholdDays,
                    daysToNextLevel = level.daysToNextLevel,
                    availableMakeupCards = card.available,
                    makeupCardMax = config?.makeupCardMax ?: card.maxAvailable,
                    breakGapDays = config?.breakGapDays ?: current.breakGapDays,
                    levelTable = levelTable,
                    error = null
                )
            }

            // 落缓存：仅供离线/首屏展示，失败不影响页面
            runCatching {
                userProgressRepository.upsert(
                    UserProgressEntity(
                        userId = UserProgressRepository.DEFAULT_USER_ID,
                        balance = overview.balance,
                        levelCode = level.levelCode,
                        levelName = level.levelName,
                        continuousDays = level.continuousDays,
                        nextLevelName = level.nextLevelName,
                        nextLevelThresholdDays = level.nextLevelThresholdDays,
                        daysToNextLevel = level.daysToNextLevel,
                        availableMakeupCards = card.available,
                        gapDays = level.gapDays,
                        breakDeadlineDate = level.breakDeadlineDate,
                        highestLevelCode = level.highestLevelCode,
                        updatedAt = System.currentTimeMillis()
                    )
                )
            }
        }
    }
}
