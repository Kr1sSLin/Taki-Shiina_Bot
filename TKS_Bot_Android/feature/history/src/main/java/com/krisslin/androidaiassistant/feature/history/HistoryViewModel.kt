package com.krisslin.androidaiassistant.feature.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.MemoryPermissionSync
import com.krisslin.androidaiassistant.core.network.api.MemoryCorrectionHttpRequest
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import retrofit2.HttpException
import java.util.UUID
import javax.inject.Inject

data class HistoryUiState(
    val notifications: List<BotNotificationEntity> = emptyList(),
    val unreadCount: Int = 0,
    val userFacts: List<UserFactEntity> = emptyList(),
    val selectedFact: UserFactEntity? = null,
    val detailContent: String? = null,
    val detailSource: String? = null,
    val operationState: String? = null,
    val detailMessage: String? = null,
    val correctionContent: String = "",
    val canCorrect: Boolean = false,
    val correctionBusy: Boolean = false
)

@HiltViewModel
class HistoryViewModel @Inject constructor(
    private val botNotificationRepository: BotNotificationRepository,
    private val userFactRepository: UserFactRepository,
    private val chatApi: ChatApi,
    private val memoryPermissionSync: MemoryPermissionSync,
    private val authRepository: AuthRepository
) : ViewModel() {

    private val _uiState = MutableStateFlow(HistoryUiState())
    val uiState: StateFlow<HistoryUiState> = _uiState.asStateFlow()

    init {
        viewModelScope.launch {
            combine(
                botNotificationRepository.observeAll(),
                botNotificationRepository.observeUnreadCount(),
                authRepository.tokenState.flatMapLatest { token ->
                    if (token is TokenState.Authenticated) {
                        authRepository.getUserId()?.let(userFactRepository::observeByUser) ?: flowOf(emptyList())
                    } else flowOf(emptyList())
                }
            ) { list, unread, facts ->
                HistoryUiState(
                    notifications = list,
                    unreadCount = unread,
                    userFacts = facts
                )
            }.collect { state ->
                _uiState.update { current ->
                    val stillSelected = current.selectedFact?.takeIf { selected ->
                        state.userFacts.any { it.factId == selected.factId && it.userId == selected.userId }
                    }
                    state.copy(
                        selectedFact = stillSelected,
                        detailContent = if (stillSelected != null) current.detailContent else null,
                        detailSource = if (stillSelected != null) current.detailSource else null,
                        operationState = if (stillSelected != null) current.operationState else null,
                        detailMessage = if (stillSelected != null) current.detailMessage else null,
                        correctionContent = if (stillSelected != null) current.correctionContent else "",
                        canCorrect = if (stillSelected != null) current.canCorrect else false,
                        correctionBusy = if (stillSelected != null) current.correctionBusy else false
                    )
                }
            }
        }
        viewModelScope.launch { memoryPermissionSync.flush() }
    }

    fun selectFact(fact: UserFactEntity) {
        _uiState.update { it.copy(selectedFact = fact, detailContent = fact.fact, detailSource = fact.sourceMessageId,
            detailMessage = null, correctionContent = "", canCorrect = false) }
        refreshSelectedFact()
    }

    fun closeFact() {
        _uiState.update { it.copy(selectedFact = null, detailContent = null, detailSource = null,
            operationState = null, detailMessage = null, correctionContent = "", canCorrect = false) }
    }

    fun refreshSelectedFact(successMessage: String? = null) {
        val selected = _uiState.value.selectedFact ?: return
        viewModelScope.launch {
            val operationState = memoryPermissionSync.state(selected.factId)
            _uiState.update { current ->
                if (current.selectedFact?.factId == selected.factId) current.copy(operationState = operationState) else current
            }
            runCatching { chatApi.memoryFactDetail(selected.factId) }
                .onSuccess { response ->
                    val data = response.getAsJsonObject("data") ?: return@onSuccess
                    val content = data.get("content")?.asString.orEmpty()
                    val source = data.getAsJsonArray("sources")?.firstOrNull()
                        ?.asJsonObject?.get("sourceMessageId")?.asString
                    val updated = selected.copy(
                        fact = content,
                        revision = data.get("revision")?.asLong ?: selected.revision,
                        revisionId = data.get("revisionId")?.takeIf { !it.isJsonNull }?.asString,
                        permissionVersion = data.get("permissionVersion")?.asLong ?: selected.permissionVersion,
                        enabled = data.get("enabled")?.asBoolean ?: selected.enabled,
                        stage = data.get("stage")?.asString ?: selected.stage,
                        truthStatus = data.get("truthStatus")?.asString ?: selected.truthStatus,
                        score = data.get("score")?.asDouble ?: selected.score,
                        sourceMessageId = source ?: selected.sourceMessageId,
                        temporalValidity = data.get("temporalValidity")?.toString()
                    )
                    userFactRepository.upsertAll(listOf(updated))
                    _uiState.update { current ->
                        if (current.selectedFact?.factId == selected.factId) current.copy(
                            selectedFact = updated, detailContent = content, detailSource = source,
                            canCorrect = data.get("domain")?.asString == "user_fact", detailMessage = successMessage
                        ) else current
                    }
                }
                .onFailure {
                    _uiState.update { current ->
                        if (current.selectedFact?.factId == selected.factId)
                            current.copy(detailMessage = "离线详情；操作会待同步") else current
                    }
                }
        }
    }

    fun setCorrectionContent(value: String) {
        _uiState.update { it.copy(correctionContent = value.take(2000)) }
    }

    fun correctSelectedFact() {
        val state = _uiState.value
        val fact = state.selectedFact ?: return
        val content = state.correctionContent.trim()
        if (!state.canCorrect || state.correctionBusy || content.isEmpty() || content == state.detailContent) return
        viewModelScope.launch {
            _uiState.update { it.copy(correctionBusy = true, detailMessage = null) }
            runCatching {
                chatApi.correctMemoryFact(fact.factId, MemoryCorrectionHttpRequest(
                    content, fact.revision, UUID.randomUUID().toString()))
            }.onSuccess {
                if (_uiState.value.selectedFact?.factId == fact.factId) {
                    _uiState.update { it.copy(correctionContent = "") }
                    refreshSelectedFact("纠正已同步到服务端")
                }
            }.onFailure { error ->
                val message = if (error is HttpException && error.code() == 409)
                    "服务端版本已变化；请核对最新状态后重新操作"
                else "纠正未提交；请检查连接后重试"
                if (_uiState.value.selectedFact?.factId == fact.factId) refreshSelectedFact(message)
            }
            _uiState.update { it.copy(correctionBusy = false) }
        }
    }

    fun setFactEnabled(enabled: Boolean) {
        val fact = _uiState.value.selectedFact ?: return
        viewModelScope.launch {
            val queued = memoryPermissionSync.queue(fact.factId, fact.userId, enabled, fact.revision)
            if (!queued) {
                _uiState.update { it.copy(detailMessage = "已有待同步操作，请先完成同步") }
                return@launch
            }
            _uiState.update { it.copy(operationState = "pending", detailMessage = "待同步，其他设备尚未生效") }
            memoryPermissionSync.flush()
            when (memoryPermissionSync.state(fact.factId)) {
                "applied" -> refreshSelectedFact()
                "conflict" -> {
                    _uiState.update { it.copy(operationState = "conflict", detailMessage = "服务端版本已变化；请核对最新状态后重新操作") }
                    refreshSelectedFact()
                }
            }
        }
    }

    fun markAsRead(id: String) {
        viewModelScope.launch {
            botNotificationRepository.markAsRead(id)
        }
    }

    fun markAllAsRead() {
        viewModelScope.launch {
            botNotificationRepository.markAllAsRead()
        }
    }
}
