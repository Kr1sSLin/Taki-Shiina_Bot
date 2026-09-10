package com.krisslin.androidaiassistant.core.network.api

import com.google.gson.annotations.SerializedName
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.POST
import retrofit2.http.Query

/**
 * 互动礼物 · 积分 · 等级 相关接口（PRD §8）。
 *
 * 统一响应信封 `{code, message, data, traceId}`；业务失败返回 HTTP 200 + code != 0。
 * 鉴权复用现有 JWT 拦截器（[com.krisslin.androidaiassistant.core.network.auth.AuthInterceptor]）。
 */
interface GamificationApi {

    /** PRD FR-1/FR-4/FR-5：互动菜单物品列表（按 sortOrder 平铺，无每日上限）。 */
    @GET("interaction/items")
    suspend fun interactionItems(
        @Header("Authorization") authorization: String? = null
    ): ApiEnvelope<InteractionItemsData>

    /** PRD FR-2/FR-3：发送互动物品；requestId 为幂等键（FR-13）。 */
    @POST("interaction/send")
    suspend fun interactionSend(
        @Header("Authorization") authorization: String? = null,
        @Body request: InteractionSendRequest
    ): ApiEnvelope<InteractionSendData>

    /** 余额 + 等级 + 补签卡聚合（客户端首屏）。 */
    @GET("points/overview")
    suspend fun pointsOverview(
        @Header("Authorization") authorization: String? = null
    ): ApiEnvelope<PointsOverviewData>

    /** PRD FR-11：积分流水（分页）。 */
    @GET("points/history")
    suspend fun pointsHistory(
        @Header("Authorization") authorization: String? = null,
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 20,
        @Query("reasonCode") reasonCode: String? = null
    ): ApiEnvelope<PointsHistoryData>

    /** PRD FR-15：当前等级、连续陪伴天数、升级进度。 */
    @GET("level/status")
    suspend fun levelStatus(
        @Header("Authorization") authorization: String? = null
    ): ApiEnvelope<LevelStatusDto>

    /** 等级阈值表与规则（后台可配置，无需发版）。 */
    @GET("level/config")
    suspend fun levelConfig(
        @Header("Authorization") authorization: String? = null
    ): ApiEnvelope<LevelConfigData>

    /** PRD FR-19：当前可用补签卡数量。 */
    @GET("points/makeup-card")
    suspend fun makeupCardSummary(
        @Header("Authorization") authorization: String? = null
    ): ApiEnvelope<MakeupCardSummaryDto>

    /** PRD FR-22：可补签日期候选（任意历史缺口）。 */
    @GET("points/makeup-card/candidates")
    suspend fun makeupCardCandidates(
        @Header("Authorization") authorization: String? = null,
        @Query("limit") limit: Int = 120
    ): ApiEnvelope<MakeupCardCandidatesData>

    /** PRD FR-20：用户主动点击使用补签卡（系统绝不自动使用）。 */
    @POST("points/makeup-card/use")
    suspend fun makeupCardUse(
        @Header("Authorization") authorization: String? = null,
        @Body request: MakeupCardUseRequest
    ): ApiEnvelope<MakeupCardUseData>

    /** PRD FR-21：补签卡发放/使用历史（核对跨月结转）。 */
    @GET("points/makeup-card/history")
    suspend fun makeupCardHistory(
        @Header("Authorization") authorization: String? = null,
        @Query("page") page: Int = 1,
        @Query("pageSize") pageSize: Int = 20
    ): ApiEnvelope<MakeupCardHistoryData>
}

data class ApiEnvelope<T>(
    val code: Int,
    val message: String?,
    val data: T?,
    val traceId: String?
)

// ==================== 互动菜单 ====================

data class InteractionItemsData(
    val balance: Int = 0,
    val dailyLimit: Int? = null,
    val items: List<InteractionItemDto> = emptyList()
)

data class InteractionItemDto(
    val id: String,
    val name: String,
    val icon: String = "",
    val iconUrl: String = "",
    val costPoints: Int = 0,
    val sortOrder: Int = 0,
    val affordable: Boolean = false
)

data class InteractionSendRequest(
    val itemId: String,
    val requestId: String,
    /** O5 附言：送礼时输入框里的文字，随礼物一起交给模型（可为空）。 */
    val text: String? = null
)

data class InteractionSendData(
    val success: Boolean = false,
    val itemId: String? = null,
    val requestId: String? = null,
    val balance: Int = 0,
    val charged: Int = 0,
    val refunded: Boolean = false,
    val duplicate: Boolean = false,
    val item: InteractionItemDto? = null,
    val reply: String? = null,
    val messageId: String? = null,
    val fallbackText: String? = null,
    val errorCode: String? = null,
    val messageText: String? = null
)

// ==================== 积分 / 等级 ====================

data class PointsOverviewData(
    val balance: Int = 0,
    val balanceUpdatedAt: Long? = null,
    val level: LevelStatusDto = LevelStatusDto(),
    val makeupCard: MakeupCardSummaryDto = MakeupCardSummaryDto()
)

data class LevelStatusDto(
    val levelCode: String = "NONE",
    val levelName: String = "",
    val prevLevelCode: String? = null,
    val continuousDays: Int = 0,
    val changeType: String? = null,
    val changeSource: String? = null,
    val highestLevelCode: String? = null,
    val lastValidDate: String? = null,
    val gapDays: Int? = null,
    val breakDeadlineDate: String? = null,
    val breakPending: Boolean = false,
    val nextLevelCode: String? = null,
    val nextLevelName: String? = null,
    val nextLevelThresholdDays: Int? = null,
    val daysToNextLevel: Int? = null,
    val levelUpdatedAt: Long? = null,
    val isDefaultLevel: Boolean = true,
    val availableMakeupCards: Int = 0
)

data class PointsHistoryData(
    val items: List<PointsLedgerItemDto> = emptyList(),
    val total: Int = 0,
    val page: Int = 1,
    val pageSize: Int = 20,
    val hasMore: Boolean = false
)

/** 流水行（服务端与 PRD 5.3 字段同名，故为 snake_case）。 */
data class PointsLedgerItemDto(
    val id: Long = 0,
    @SerializedName("user_id") val userId: String = "",
    @SerializedName("change_amount") val changeAmount: Int = 0,
    @SerializedName("reason_code") val reasonCode: String = "",
    @SerializedName("balance_after") val balanceAfter: Int = 0,
    @SerializedName("related_item_id") val relatedItemId: String? = null,
    @SerializedName("created_at") val createdAt: Long = 0,
    @SerializedName("business_date") val businessDate: String? = null,
    val note: String? = null
)

data class LevelConfigData(
    val levels: List<LevelConfigItemDto> = emptyList(),
    val defaultLevelCode: String = "NONE",
    val defaultLevelName: String = "",
    val makeupCardMax: Int = 12,
    val breakGapDays: Int = 5,
    val warningGapDays: List<Int> = emptyList()
)

/**
 * 等级阈值行。
 *
 * ⚠️ 服务端 `/api/v1/level/config` 的 `levels` 直接使用 PRD 5.5 的表字段（snake_case），
 * 必须用 [SerializedName] 映射，否则 Gson 会全部落成默认值（曾因此触发等级列表重复 key 崩溃）。
 */
data class LevelConfigItemDto(
    @SerializedName("level_code") val levelCode: String = "",
    @SerializedName("level_name") val levelName: String = "",
    @SerializedName("threshold_days") val thresholdDays: Int = 0,
    @SerializedName("sort_order") val sortOrder: Int = 0
)

// ==================== 补签卡 ====================

data class MakeupCardSummaryDto(
    val available: Int = 0,
    val used: Int = 0,
    val totalGranted: Int = 0,
    val maxAvailable: Int = 12,
    val monthlyGrant: Int = 1,
    val lastGrantedMonth: String? = null,
    val currentMonthGranted: Boolean = false,
    val atLimit: Boolean = false
)

/** 补签卡库存行（服务端字段与 PRD 5.4 同名，故为 snake_case）。 */
data class MakeupCardDto(
    val id: Long = 0,
    @SerializedName("user_id") val userId: String = "",
    @SerializedName("granted_month") val grantedMonth: String = "",
    val status: String = "AVAILABLE",
    @SerializedName("used_for_date") val usedForDate: String? = null,
    @SerializedName("used_at") val usedAt: Long? = null,
    @SerializedName("created_at") val createdAt: Long = 0
)

data class MakeupCardHistoryData(
    val items: List<MakeupCardDto> = emptyList(),
    val total: Int = 0,
    val page: Int = 1,
    val pageSize: Int = 20,
    val hasMore: Boolean = false,
    val summary: MakeupCardSummaryDto = MakeupCardSummaryDto()
)

data class MakeupCardCandidateDto(
    val date: String = "",
    val daysAgo: Int = 0
)

data class MakeupCardCandidatesData(
    val items: List<MakeupCardCandidateDto> = emptyList(),
    val total: Int = 0,
    val firstActivityDate: String? = null,
    val available: Int = 0
)

data class MakeupCardUseRequest(
    val targetDate: String
)

data class MakeupCardUseData(
    val success: Boolean = false,
    val availableCards: Int = 0,
    val targetDate: String? = null,
    val errorCode: String? = null,
    val message: String? = null,
    val card: MakeupCardDto? = null,
    val level: LevelStatusDto? = null
)
