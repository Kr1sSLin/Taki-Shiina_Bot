package com.krisslin.androidaiassistant.feature.history

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.krisslin.androidaiassistant.core.database.entity.BotNotificationEntity
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
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
    val unreadCount: Int = 0,
    val userFacts: List<UserFactEntity> = emptyList()
)

@HiltViewModel
class HistoryViewModel @Inject constructor(
    private val botNotificationRepository: BotNotificationRepository,
    private val userFactRepository: UserFactRepository
) : ViewModel() {

    private val _uiState = MutableStateFlow(HistoryUiState())
    val uiState: StateFlow<HistoryUiState> = _uiState.asStateFlow()

    init {
        viewModelScope.launch {
            combine(
                botNotificationRepository.observeAll(),
                botNotificationRepository.observeUnreadCount(),
                userFactRepository.observeByUser("default-user")
            ) { list, unread, facts ->
                HistoryUiState(
                    notifications = list,
                    unreadCount = unread,
                    userFacts = facts
                )
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
