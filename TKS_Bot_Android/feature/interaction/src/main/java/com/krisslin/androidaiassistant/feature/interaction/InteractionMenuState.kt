package com.krisslin.androidaiassistant.feature.interaction

/**
 * 互动菜单物品行（PRD FR-4 / FR-5）。
 *
 * [affordable] 由服务端按当前余额计算，客户端据此置灰（US-1）。
 */
data class InteractionMenuItemUi(
    val id: String,
    val name: String,
    val icon: String,
    val iconUrl: String,
    val costPoints: Int,
    val affordable: Boolean
)

/** 互动菜单面板状态：余额 + 平铺物品列表 + 错误提示。 */
data class InteractionMenuUiState(
    val loading: Boolean = false,
    val balance: Int = 0,
    val items: List<InteractionMenuItemUi> = emptyList(),
    val error: String? = null
)
