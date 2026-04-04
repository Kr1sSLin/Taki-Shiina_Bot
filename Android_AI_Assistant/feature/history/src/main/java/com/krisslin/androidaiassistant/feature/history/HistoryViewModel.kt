package com.krisslin.androidaiassistant.feature.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

data class HistoryUiState(
    val notifications: List<BotNotificationEntity> = emptyList(),
    val unreadCount: Int = 0
)

@HiltViewModel
class HistoryViewModel @Inject constructor(
    private val botNotificationRepository: BotNotificationRepository
) : ViewModel() {

    private val _uiState = MutableStateFlow(HistoryUiState())
    val uiState: StateFlow<HistoryUiState> = _uiState.asStateFlow()

    init {
        viewModelScope.launch {
            combine(
                botNotificationRepository.observeAll(),
                botNotificationRepository.observeUnreadCount()
            ) { list, unread ->
                HistoryUiState(notifications = list, unreadCount = unread)
            }.collect { state ->
                _uiState.value = state
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
