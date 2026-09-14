package com.krisslin.androidaiassistant.feature.chat

import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import android.util.Base64
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.google.gson.Gson
import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity
import com.krisslin.androidaiassistant.core.database.entity.ChatMessageEntity
import com.krisslin.androidaiassistant.core.database.entity.UserFactEntity
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.ContentType
import com.krisslin.androidaiassistant.core.database.repository.MessageRole
import com.krisslin.androidaiassistant.core.database.repository.MessageStatus
import com.krisslin.androidaiassistant.core.database.repository.SyncOutcome
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.database.repository.ModelProvider
import com.krisslin.androidaiassistant.core.database.repository.UserProgressRepository
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.GamificationApi
import com.krisslin.androidaiassistant.core.network.api.InteractionSendRequest
import com.krisslin.androidaiassistant.core.network.api.InteractionSendData
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenState
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.ChatImagePayload
import com.krisslin.androidaiassistant.core.network.ws.ChatMessagePayload
import com.krisslin.androidaiassistant.core.network.ws.ChatMessageRequest
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessage
import com.krisslin.androidaiassistant.core.network.ws.IncomingMessageParser
import com.krisslin.androidaiassistant.core.network.ws.WebSocketEvent
import com.krisslin.androidaiassistant.core.push.ReminderScheduler
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.inject.Inject

data class ChatAttachmentUi(
    val id: String,
    val localUri: String,
    val mimeType: String,
    val fileSize: Long,
    val width: Int? = null,
    val height: Int? = null
)

data class ChatMessageUi(
    val id: String,
    val role: String,
    val content: String,
    val contentType: String = ContentType.TEXT,
    val modelProvider: String = ModelProvider.DEEPSEEK,
    val attachments: List<ChatAttachmentUi> = emptyList(),
    val timestamp: Long = System.currentTimeMillis(),
    val status: String = MessageStatus.RECEIVED,
    val errorCode: String? = null,
    val isStreaming: Boolean = false
)

/**
 * 连接状态
 */
enum class ConnectionStatus {
    CONNECTING,     // 连接中
    CONNECTED,      // 已连接
    DISCONNECTED    // 已断开
}

/**
 * Bot 活动状态（用于顶部提示显示）
 */
enum class BotActivityStatus {
    IDLE,       // 空闲：不显示任何内容
    SENDING,    // 发送中：用户消息已发出，等待服务器确认
    TYPING      // 输入中：Bot 正在生成回复
}

data class ChatUiState(
    val connectionStatus: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    val botActivity: BotActivityStatus = BotActivityStatus.IDLE,
    val botStage: String? = null,
    val input: String = "",
    val error: String? = null,
    val selectedImages: List<ChatAttachmentUi> = emptyList(),
    val messages: List<ChatMessageUi> = emptyList(),
    // 互动积分 · 等级体系：离线缓存展示（后端为准，见 UserProgressRepository）
    val balance: Int = 0,
    val levelName: String = "",
    val continuousDays: Int = 0,
    // 升级庆祝 / 等级恢复提示（PRD FR-16 / EDGE-11）
    val levelCelebration: LevelCelebrationUi? = null
)

/**
 * 升级/等级恢复庆祝信息。
 * changeType：UPGRADE（首次达成）/ RESTORE（补签回溯挽回）。
 */
data class LevelCelebrationUi(
    val changeType: String,
    val levelName: String,
    val continuousDays: Int,
    val nextLevelName: String? = null,
    val daysToNextLevel: Int? = null
)

/**
 * Side Effect 事件
 */
sealed interface ChatSideEffect {
    data object NavigateToLogin : ChatSideEffect
    data class ShowToast(val message: String) : ChatSideEffect
}

@HiltViewModel
class ChatViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val chatApi: ChatApi,
    private val gamificationApi: GamificationApi,
    private val chatRepository: ChatRepository,
    private val userFactRepository: UserFactRepository,
    private val botNotificationRepository: BotNotificationRepository,
    private val userProgressRepository: UserProgressRepository,
    private val authRepository: AuthRepository,
    private val botWebSocketClient: BotWebSocketClient,
    private val reminderScheduler: ReminderScheduler,
    private val gson: Gson
) : ViewModel() {
    private companion object {
        const val MAX_IMAGE_BYTES = 20 * 1024 * 1024
        const val MAX_IMAGE_TIPS = "图片超过 20MB 限制"
    }

    private val _uiState = MutableStateFlow(ChatUiState())
    val uiState: StateFlow<ChatUiState> = _uiState.asStateFlow()

    private val _sideEffects = MutableSharedFlow<ChatSideEffect>()
    val sideEffects: SharedFlow<ChatSideEffect> = _sideEffects.asSharedFlow()

    private val messageParser = IncomingMessageParser(gson)

    // 当前会话 ID
    private val sessionId: String = "default_session"

    // 流式消息内容缓存: requestId -> 累积内容
    private val streamingContentCache = ConcurrentHashMap<String, StringBuilder>()

    // 流式消息超时任务: requestId -> Job
    private val streamingTimeoutJobs = ConcurrentHashMap<String, Job>()
    private val pendingRequestIds = ConcurrentHashMap.newKeySet<String>()

    // 流式消息处理锁，防止竞态条件
    private val streamingMutex = Mutex()

    // 追踪已收到首帧的 requestId（用于判断是否切换为 TYPING 状态）
    private val receivedFirstChunk = ConcurrentHashMap.newKeySet<String>()

    // 已由 WS 推送落库的回复 id（互动 HTTP 兜底据此避免重复写入）
    private val deliveredBotMessageIds = ConcurrentHashMap.newKeySet<String>()

    // 流式消息超时时间（毫秒）
    private val streamingTimeoutMs = 150_000L

    private var lastSyncedTimestampMs: Long = 0L
    private var lastSyncedFactTimestampMs: Long = 0L
    private var manualReconnectPendingFullSync: Boolean = false

    private data class OutgoingImage(
        val ui: ChatAttachmentUi,
        val dataBase64: String
    )

    private data class OutgoingPayload(
        val text: String,
        val images: List<OutgoingImage>
    )

    init {
        loadHistoryFromDb()
        initializeFactSyncCursor()
        observeAuthState()
        observeWebSocketEvents()
        observeLocalProgressCache()
        // 进入聊天页即主动补拉历史，不再只依赖 Connected 事件（该事件可能先于订阅者发出而丢失）
        syncHistoryFromServer()
        repairOrphanedAttachments()
    }

    /**
     * 观察积分/等级本地缓存（PRD §3.1：后端为唯一数据源，本地仅做展示缓存）。
     * 供聊天页顶部的等级/积分入口与离线展示使用，真正的数据以后端返回与 WS 事件为准。
     */
    private fun observeLocalProgressCache() {
        viewModelScope.launch {
            userProgressRepository.observe().collect { cached ->
                if (cached == null) return@collect
                _uiState.update {
                    it.copy(
                        balance = cached.balance,
                        levelName = cached.levelName,
                        continuousDays = cached.continuousDays
                    )
                }
            }
        }
    }

    /**
     * 数据修复：早期版本 markUserMessageSent 用 INSERT OR REPLACE 更新消息，
     * 触发外键 CASCADE 把已保存的附件行删掉，导致历史图片消息没有附件。
     * 启动时把 files/chat_attachments 下未被引用的文件按时间就近匹配回图片消息。
     */
    private fun repairOrphanedAttachments() {
        viewModelScope.launch {
            runCatching {
                val dir = java.io.File(context.filesDir, "chat_attachments")
                val orphanFiles = dir.listFiles()?.filter { it.isFile } ?: return@runCatching
                if (orphanFiles.isEmpty()) return@runCatching

                val imageMessages = chatRepository.getMessages(sessionId).filter {
                    it.role == MessageRole.USER &&
                        it.contentType == ContentType.IMAGE &&
                        it.status != MessageStatus.ERROR
                }
                if (imageMessages.isEmpty()) return@runCatching

                val linkedPaths = chatRepository
                    .getAttachmentsByMessageIds(imageMessages.map { it.messageId })
                    .mapNotNull { runCatching { Uri.parse(it.localUri).path }.getOrNull() }
                var remaining = orphanFiles.filter { it.absolutePath !in linkedPaths }
                if (remaining.isEmpty()) return@runCatching

                val windowMs = 10 * 60 * 1000L
                for (msg in imageMessages) {
                    if (remaining.isEmpty()) break
                    val best = remaining.minByOrNull { kotlin.math.abs(it.lastModified() - msg.timestamp) }
                    if (best == null || kotlin.math.abs(best.lastModified() - msg.timestamp) > windowMs) continue
                    val opts = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                    BitmapFactory.decodeFile(best.absolutePath, opts)
                    chatRepository.saveMessageAttachments(
                        listOf(
                            ChatAttachmentEntity(
                                attachmentId = UUID.randomUUID().toString(),
                                messageId = msg.messageId,
                                sessionId = sessionId,
                                mimeType = if (best.extension.equals("png", true)) "image/png" else "image/jpeg",
                                localUri = Uri.fromFile(best).toString(),
                                fileSize = best.length(),
                                width = opts.outWidth.takeIf { it > 0 },
                                height = opts.outHeight.takeIf { it > 0 }
                            )
                        )
                    )
                    remaining = remaining - best
                }
            }
        }
    }

    /**
     * 从 Room 数据库加载历史消息（消息表与附件表联动，附件保存后立即可见）
     */
    private fun loadHistoryFromDb() {
        viewModelScope.launch {
            // 清理历史遗留的空 bot 消息（后端旧版空回复落库的脏数据）
            runCatching { chatRepository.deleteBlankBotMessages() }
            chatRepository.observeMessagesWithAttachments(sessionId).collect { pairs ->
                val uiMessages = pairs.map { (entity, attachments) ->
                    entity.toUiModel(
                        attachments.map { att ->
                            ChatAttachmentUi(
                                id = att.attachmentId,
                                localUri = att.localUri,
                                mimeType = att.mimeType,
                                fileSize = att.fileSize,
                                width = att.width,
                                height = att.height
                            )
                        }
                    )
                }
                lastSyncedTimestampMs = pairs.maxOfOrNull { it.first.timestamp } ?: lastSyncedTimestampMs
                _uiState.update { it.copy(messages = uiMessages) }
            }
        }
    }

    private fun normalizeHistoryOnResume() {
        viewModelScope.launch {
            if (pendingRequestIds.isNotEmpty() || streamingContentCache.isNotEmpty()) {
                return@launch
            }
            chatRepository.normalizeStreamingMessages(sessionId)
        }
    }

    private fun initializeFactSyncCursor() {
        viewModelScope.launch {
            // 本地库仅保存当前登录用户的事实，直接取全局最新时间戳
            lastSyncedFactTimestampMs = userFactRepository.getLatestTimestamp()
        }
    }

    /**
     * 观察认证状态变化
     */
    private fun observeAuthState() {
        viewModelScope.launch {
            authRepository.tokenState.collect { state ->
                when (state) {
                    is TokenState.Unauthenticated -> {
                        _sideEffects.emit(ChatSideEffect.NavigateToLogin)
                    }
                    is TokenState.Authenticated -> {
                        // WebSocket 连接由 WebSocketService 管理
                    }
                }
            }
        }
    }

    /**
     * 观察 WebSocket 事件
     */
    private fun observeWebSocketEvents() {
        viewModelScope.launch {
            botWebSocketClient.events.collect { event ->
                when (event) {
                    is WebSocketEvent.Connected -> {
                        _uiState.update { it.copy(connectionStatus = ConnectionStatus.CONNECTED, error = null) }
                        syncHistoryFromServer(force = manualReconnectPendingFullSync)
                        if (manualReconnectPendingFullSync) {
                            manualReconnectPendingFullSync = false
                        }
                        syncMemoryFactsFromServer()
                    }
                    is WebSocketEvent.Disconnected -> {
                        _uiState.update { it.copy(connectionStatus = ConnectionStatus.DISCONNECTED) }
                        handleConnectionLost()
                    }
                    is WebSocketEvent.Message -> {
                        handleIncomingMessage(event.text)
                    }
                    is WebSocketEvent.Failure -> {
                        _uiState.update { 
                            it.copy(
                                connectionStatus = ConnectionStatus.DISCONNECTED,
                                error = "连接失败: ${event.throwable.message}"
                            ) 
                        }
                        handleConnectionLost()
                    }
                }
            }
        }
    }

    /**
     * 处理接收到的 WebSocket 消息
     */
    private fun handleIncomingMessage(raw: String) {
        viewModelScope.launch {
            when (val message = messageParser.parse(raw)) {
                is IncomingMessage.Reply -> {
                    handleReply(message)
                }
                is IncomingMessage.ReplyStream -> {
                    handleReplyStream(message)
                }
                is IncomingMessage.BotError -> {
                    handleBotError(message)
                }
                is IncomingMessage.MemoryFactCreated -> {
                    handleMemoryFactCreated(message)
                }
                is IncomingMessage.UserEcho -> {
                    handleUserEcho(message)
                }
                is IncomingMessage.Typing -> {
                    handleTyping(message)
                }
                is IncomingMessage.PointsChanged -> {
                    handlePointsChanged(message)
                }
                is IncomingMessage.LevelChanged -> {
                    handleLevelChanged(message)
                }
                is IncomingMessage.StreakWarning -> {
                    handleStreakWarning(message)
                }
                is IncomingMessage.MakeupCardChanged -> {
                    handleMakeupCardChanged(message)
                }
                is IncomingMessage.AuthExpired -> {
                    handleAuthExpired()
                }
                is IncomingMessage.Unknown -> {
                    // 忽略未知消息
                }
            }
        }
    }

    /**
     * 处理 Bot 输入状态（可选 stage: "vision" / "generating"）
     */
    private fun handleTyping(message: IncomingMessage.Typing) {
        _uiState.update {
            it.copy(
                botActivity = BotActivityStatus.TYPING,
                botStage = message.payload.stage
            )
        }
    }

    // ==================== 互动礼物 · 积分 · 等级（PRD 4.1 / 4.4 / 4.5） ====================

    /**
     * 发送互动物品（PRD FR-2 / FR-3 / 6.3）。
     *
     * 客户端只做菜单置灰级别的本地校验，最终以后端返回为准；
     * `requestId` 同时作为服务端幂等键（FR-13），重复点击不会重复扣分。
     * 回复优先由 WS 推送展示，此处仅在 WS 断线时用 HTTP 响应兜底落库。
     */
    fun sendInteraction(itemId: String) {
        val requestId = UUID.randomUUID().toString()
        // O5：送礼时把输入框里的文字作为「附言」一起送出（成功后才清空，失败保留让用户可以照常发送）
        val attachment = _uiState.value.input.trim()
        viewModelScope.launch {
            _uiState.update { it.copy(botActivity = BotActivityStatus.SENDING, error = null) }
            val envelope = runCatching {
                gamificationApi.interactionSend(
                    request = InteractionSendRequest(
                        itemId = itemId,
                        requestId = requestId,
                        text = attachment.ifBlank { null }
                    )
                )
            }.getOrNull()

            val data = envelope?.data
            if (envelope == null) {
                _sideEffects.tryEmit(ChatSideEffect.ShowToast("互动发送失败，请检查网络后重试"))
                resetBotActivity()
                return@launch
            }
            if (envelope.code != 0 || data == null || !data.success) {
                val message = when (data?.errorCode) {
                    "INSUFFICIENT_POINTS" -> "积分不足，先和立希多聊几句吧"
                    "ITEM_NOT_FOUND" -> "这个物品已经下架了"
                    "AI_FAILED_REFUNDED" -> "立希这次没接住，积分已退回"
                    else -> envelope.message ?: "互动发送失败"
                }
                _sideEffects.tryEmit(ChatSideEffect.ShowToast(message))
                resetBotActivity()
                return@launch
            }

            // 附言已随礼物送出，清空输入框（仅当用户没有在这期间改过内容）
            if (attachment.isNotBlank() && _uiState.value.input.trim() == attachment) {
                _uiState.update { it.copy(input = "") }
            }
            persistInteractionLocally(requestId, data, attachment)
            // 服务端会通过 WS 推送同一条回复（同 messageId，Room REPLACE 去重）
            _uiState.update { it.copy(botActivity = BotActivityStatus.SENDING) }
        }
    }

    /** 关闭升级/等级恢复庆祝弹窗。 */
    fun dismissLevelCelebration() {
        _uiState.update { it.copy(levelCelebration = null) }
    }

    private fun resetBotActivity() {
        _uiState.update {
            it.copy(
                botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING,
                botStage = null
            )
        }
    }

    private suspend fun persistInteractionLocally(
        requestId: String,
        data: InteractionSendData,
        attachment: String = ""
    ) {
        val item = data.item
        val itemLabel = listOfNotNull(
            item?.icon?.takeIf { it.isNotBlank() },
            item?.name?.takeIf { it.isNotBlank() }
        ).joinToString(" ")
        if (itemLabel.isNotBlank()) {
            // 与服务端 timeline 使用同一 messageId 与同一文案（含附言），历史补拉时不会重复也不会不一致
            val userMessageId = "interaction_user_$requestId"
            val content = if (attachment.isBlank()) itemLabel else "$itemLabel · $attachment"
            chatRepository.saveUserMessage(
                messageId = userMessageId,
                sessionId = sessionId,
                content = content,
                contentType = ContentType.TEXT
            )
            chatRepository.markUserMessageSent(userMessageId)
        }
        val reply = data.reply
        val messageId = data.messageId
        if (!reply.isNullOrBlank() && !messageId.isNullOrBlank() && messageId !in deliveredBotMessageIds) {
            // WS 未送达时的兜底；分条规则与 messageId 与 WS/历史补拉完全一致
            saveBotReply(
                messageId = messageId,
                content = reply,
                timestamp = System.currentTimeMillis()
            )
        }
    }

    /** 积分变动（PRD FR-11）：以后端为准刷新本地缓存。 */
    private fun handlePointsChanged(message: IncomingMessage.PointsChanged) {
        val payload = message.payload
        val balance = payload.balance ?: payload.balanceAfter
        viewModelScope.launch {
            runCatching { userProgressRepository.applyBalance(balance) }
        }
    }

    /**
     * 等级变化（PRD FR-16 / EDGE-11）。
     * 首次达成为“升级庆祝”，补签回溯挽回为“等级已恢复”，两者文案区分。
     */
    private fun handleLevelChanged(message: IncomingMessage.LevelChanged) {
        val payload = message.payload
        viewModelScope.launch {
            runCatching {
                userProgressRepository.applyLevel(
                    levelCode = payload.levelCode,
                    levelName = payload.levelName,
                    continuousDays = payload.continuousDays,
                    nextLevelName = payload.nextLevelName,
                    nextLevelThresholdDays = payload.nextLevelThresholdDays,
                    daysToNextLevel = payload.daysToNextLevel,
                    highestLevelCode = payload.highestLevelCode,
                    gapDays = payload.gapDays,
                    breakDeadlineDate = payload.breakDeadlineDate
                )
            }
        }
        when (payload.changeType) {
            "UPGRADE", "RESTORE" -> _uiState.update {
                it.copy(
                    levelCelebration = LevelCelebrationUi(
                        changeType = payload.changeType ?: "UPGRADE",
                        levelName = payload.levelName,
                        continuousDays = payload.continuousDays,
                        nextLevelName = payload.nextLevelName,
                        daysToNextLevel = payload.daysToNextLevel
                    )
                )
            }
            "RESET" -> _sideEffects.tryEmit(
                ChatSideEffect.ShowToast("连续陪伴中断，等级已重置；积分不受影响，可用补签卡挽回")
            )
            else -> Unit
        }
    }

    /** 断签提前提醒（PRD FR-18）：应用内提示，本地通知由 WebSocketService 负责。 */
    private fun handleStreakWarning(message: IncomingMessage.StreakWarning) {
        val payload = message.payload
        _sideEffects.tryEmit(
            ChatSideEffect.ShowToast(
                "已经 ${payload.gapDays} 天没和立希聊天了，${payload.remainingDays} 天后等级会归零"
            )
        )
    }

    /** 补签卡库存变动（PRD FR-19 / FR-21）。 */
    private fun handleMakeupCardChanged(message: IncomingMessage.MakeupCardChanged) {
        val payload = message.payload
        viewModelScope.launch {
            runCatching { userProgressRepository.applyMakeupCardCount(payload.available) }
            if (payload.reason == "MONTHLY_GRANT") {
                _sideEffects.tryEmit(
                    ChatSideEffect.ShowToast("本月补签卡已到账，当前可用 ${payload.available}/${payload.maxAvailable} 张")
                )
            }
        }
    }

    /**
     * 处理完整回复（非流式）
     */
    private suspend fun handleReply(message: IncomingMessage.Reply) {        val requestId = message.requestId ?: return
        val payload = message.payload

        // 收到回复，切换为 TYPING 状态
        _uiState.update { it.copy(botActivity = BotActivityStatus.TYPING, botStage = null) }

        // 取消超时任务
        streamingTimeoutJobs.remove(requestId)?.cancel()
        streamingContentCache.remove(requestId)

        // 标记用户消息已发送
        chatRepository.markUserMessageSent(requestId)

        if (payload.content.isNotBlank()) {
            val segments = splitBotSegments(payload.content)
            if (segments.isEmpty()) {
                chatRepository.saveBotMessage(
                    messageId = payload.messageId,
                    sessionId = sessionId,
                    content = payload.content,
                    isStreaming = false,
                    timestamp = payload.timestamp
                )
            } else {
                segments.forEachIndexed { index, segment ->
                    chatRepository.saveBotMessage(
                        messageId = "${payload.messageId}_$index",
                        sessionId = sessionId,
                        content = segment,
                        isStreaming = false,
                        timestamp = payload.timestamp + index
                    )
                }
            }
        }

        // 回复完成，移除 pendingId 并回到 IDLE
        pendingRequestIds.remove(requestId)
        receivedFirstChunk.remove(requestId)
        _uiState.update {
            it.copy(botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING, botStage = null)
        }
        syncMemoryFactsFromServer()
    }

    /**
     * 处理流式回复
     */
    private suspend fun handleReplyStream(message: IncomingMessage.ReplyStream) {
        val requestId = message.requestId ?: return
        val payload = message.payload
        val pendingMessageId = "pending_$requestId"

        streamingMutex.withLock {
            runCatching {
                if (payload.done) {
                    // 流式完成
                    streamingTimeoutJobs.remove(requestId)?.cancel()
                    val cachedContent = streamingContentCache.remove(requestId)?.toString()

                    // 标记被防抖合并的所有用户消息已发送
                    val relatedIds = payload.requestIds?.takeIf { it.isNotEmpty() } ?: listOf(requestId)
                    relatedIds.forEach { id ->
                        runCatching { chatRepository.markUserMessageSent(id) }
                        pendingRequestIds.remove(id)
                        receivedFirstChunk.remove(id)
                        streamingTimeoutJobs.remove(id)?.cancel()
                    }

                    val finalContent = payload.finalContent ?: cachedContent ?: ""
                    val finalMessageId = payload.messageId ?: "bot_$requestId"
                    val finalTimestamp = payload.timestamp ?: System.currentTimeMillis()

                    // 删除流式占位消息（如果存在）
                    if (cachedContent != null) {
                        chatRepository.deleteMessage(pendingMessageId)
                    }

                    // 保存分条气泡（空内容不落库）
                    if (finalContent.isNotBlank()) {
                        saveBotReply(
                            messageId = finalMessageId,
                            content = finalContent,
                            contentType = payload.contentType ?: ContentType.TEXT,
                            modelProvider = payload.modelProvider ?: ModelProvider.DEEPSEEK,
                            timestamp = finalTimestamp
                        )
                        markBotReplyDelivered(finalMessageId)
                    }

                    payload.timerInstruction?.let { timer ->
                        val scheduleKey = relatedIds.joinToString("_")
                        reminderScheduler.schedule(
                            requestId = scheduleKey.ifBlank { requestId },
                            target = timer.target,
                            text = timer.text
                        )
                    }

                    // 流式完成，回到 IDLE 或保持 SENDING（如有其他待处理消息）
                    _uiState.update {
                        it.copy(botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING, botStage = null)
                    }
                    syncMemoryFactsFromServer()
                } else {
                    // 流式增量
                    val delta = payload.delta
                    if (delta.isNotEmpty()) {
                        // 首帧到达时切换为 TYPING 状态
                        if (receivedFirstChunk.add(requestId)) {
                            _uiState.update { it.copy(botActivity = BotActivityStatus.TYPING, botStage = null) }
                        }

                        // 确保占位消息存在后再追加
                        if (!streamingContentCache.containsKey(requestId)) {
                            chatRepository.saveBotMessage(
                                messageId = pendingMessageId,
                                sessionId = sessionId,
                                content = delta,
                                isStreaming = true,
                                contentType = payload.contentType ?: ContentType.TEXT,
                                modelProvider = payload.modelProvider ?: ModelProvider.DEEPSEEK
                            )
                            streamingContentCache[requestId] = StringBuilder(delta)
                        } else {
                            streamingContentCache[requestId]!!.append(delta)
                            chatRepository.appendStreamingContent(pendingMessageId, delta)
                        }
                        resetStreamingTimeout(requestId, pendingMessageId)
                    }
                }
            }.onFailure { e ->
                android.util.Log.e("ChatViewModel", "Stream handling error: ${e.message}", e)
            }
        }
    }

    /**
     * 处理 Bot 错误
     */
    private suspend fun handleBotError(message: IncomingMessage.BotError) {
        val payload = message.payload
        val relatedIds = buildList {
            message.requestId?.let { add(it) }
            addAll(payload.requestIds)
        }.distinct()
        relatedIds.forEach { requestId ->
            streamingTimeoutJobs.remove(requestId)?.cancel()
            streamingContentCache.remove(requestId)
            pendingRequestIds.remove(requestId)
            receivedFirstChunk.remove(requestId)
            chatRepository.markMessageError("pending_$requestId", payload.errorCode)
            chatRepository.markMessageError(requestId, payload.errorCode)
        }
        _uiState.update { 
            it.copy(
                botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING,
                botStage = null,
                error = "${payload.errorCode}: ${payload.message}"
            ) 
        }

        botNotificationRepository.addError(
            errorCode = payload.errorCode,
            message = payload.message,
            timestamp = payload.timestamp
        )
    }

    /**
     * 处理多设备回声：本用户从其他设备发出的消息实时落库。
     *
     * 回声本身就是「服务端收到了」的凭证：发送端若已存在同 requestId 的行，
     * saveExternalMessage 会把它从 sending / error 修回 sent（内容与时间戳保留），
     * 不存在则新增。两种情况都幂等。
     */
    private suspend fun handleUserEcho(message: IncomingMessage.UserEcho) {
        val requestId = message.requestId ?: return
        val payload = message.payload
        val content = when {
            payload.content.isNotBlank() -> payload.content
            payload.imageCount > 0 -> "[图片×${payload.imageCount}]"
            else -> return
        }
        chatRepository.saveExternalMessage(
            messageId = requestId,
            sessionId = sessionId,
            role = MessageRole.USER,
            content = content,
            timestamp = payload.timestamp
        )
    }

    /**
     * 处理认证过期
     */
    private suspend fun handleAuthExpired() {
        botWebSocketClient.disconnect()

        // 尝试刷新 Token
        val newToken = authRepository.refreshToken()
        if (newToken != null) {
            botWebSocketClient.connect(newToken)
        } else {
            _sideEffects.emit(ChatSideEffect.NavigateToLogin)
        }
    }

    /**
     * 连接断开时的兜底：立即清掉所有未完成请求的状态,
     * 避免一直停留在"打字中"。服务端断线期间生成的回复会在重连后由 full sync 拉回。
     */
    private suspend fun handleConnectionLost() {
        if (pendingRequestIds.isEmpty() && streamingContentCache.isEmpty()) return
        streamingTimeoutJobs.values.forEach { it.cancel() }
        streamingTimeoutJobs.clear()
        for (requestId in pendingRequestIds.toList()) {
            runCatching { chatRepository.markMessageError("pending_$requestId", "CONNECTION_LOST") }
            runCatching { chatRepository.markMessageError(requestId, "CONNECTION_LOST") }
        }
        streamingContentCache.clear()
        pendingRequestIds.clear()
        receivedFirstChunk.clear()
        _uiState.update {
            it.copy(
                botActivity = BotActivityStatus.IDLE,
                botStage = null,
                error = "连接中断，未完成的回复已失败，重连后可查看服务端记录"
            )
        }
    }

    /**
     * 重置流式消息超时计时器
     */
    private fun resetStreamingTimeout(requestId: String, pendingMessageId: String) {
        streamingTimeoutJobs[requestId]?.cancel()
        streamingTimeoutJobs[requestId] = viewModelScope.launch {
            delay(streamingTimeoutMs)
            // 超时仍在 streaming → 标记错误
            streamingContentCache.remove(requestId)
            pendingRequestIds.remove(requestId)
            receivedFirstChunk.remove(requestId)
            chatRepository.markMessageError(pendingMessageId, "TIMEOUT")
            _uiState.update { 
                it.copy(
                    botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING,
                    botStage = null,
                    error = "AI 响应超时，请重试"
                ) 
            }
        }
    }

    fun onInputChange(value: String) {
        _uiState.update { it.copy(input = value) }
    }

    fun onPickImages(uris: List<Uri>) {
        if (uris.isEmpty()) return
        val existing = _uiState.value.selectedImages.toMutableList()
        val picked = uris.take(3 - existing.size).mapNotNull { uri ->
            createUiAttachment(uri)
        }
        _uiState.update { it.copy(selectedImages = (existing + picked).take(3)) }
    }

    fun removeSelectedImage(id: String) {
        _uiState.update { it.copy(selectedImages = it.selectedImages.filterNot { image -> image.id == id }) }
    }

    fun clearError() {
        _uiState.update { it.copy(error = null) }
    }

    /**
     * 清空当前会话（仅本地聊天记录，不影响登录态）
     */
    fun clearConversation() {
        viewModelScope.launch {
            streamingTimeoutJobs.values.forEach { it.cancel() }
            streamingTimeoutJobs.clear()
            streamingContentCache.clear()
            pendingRequestIds.clear()
            receivedFirstChunk.clear()

            chatRepository.clearSession(sessionId)
            // 防止清空后立即被历史补拉回灌
            lastSyncedTimestampMs = System.currentTimeMillis()

            _uiState.update { it.copy(botActivity = BotActivityStatus.IDLE, botStage = null, error = null, selectedImages = emptyList()) }
            _sideEffects.emit(ChatSideEffect.ShowToast("会话已清空"))
        }
    }

    /**
     * 发送文本消息
     */
    fun sendText() {
        val currentState = _uiState.value
        val current = currentState.input.trim()
        val selectedImages = currentState.selectedImages
        if (current.isEmpty() && selectedImages.isEmpty()) return

        // 检查连接状态
        if (_uiState.value.connectionStatus != ConnectionStatus.CONNECTED) {
            _uiState.update { it.copy(error = "未连接到服务器") }
            return
        }

        val requestId = UUID.randomUUID().toString()
        pendingRequestIds.add(requestId)
        _uiState.update { it.copy(input = "", selectedImages = emptyList(), botActivity = BotActivityStatus.SENDING, error = null) }

        viewModelScope.launch {
            val prepared = prepareOutgoingPayload(current, selectedImages)
            if (prepared == null) {
                pendingRequestIds.remove(requestId)
                _uiState.update { it.copy(botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING, botStage = null) }
                return@launch
            }
            val contentType = if (prepared.images.isEmpty()) ContentType.TEXT else if (prepared.text.isBlank()) ContentType.IMAGE else ContentType.MIXED
            val modelProvider = if (prepared.images.isEmpty()) ModelProvider.DEEPSEEK else ModelProvider.GEMINI

            // 保存用户消息到 DB
            chatRepository.saveUserMessage(
                messageId = requestId,
                sessionId = sessionId,
                content = prepared.text,
                contentType = contentType,
                modelProvider = modelProvider
            )
            if (prepared.images.isNotEmpty()) {
                chatRepository.saveMessageAttachments(
                    prepared.images.map {
                        com.krisslin.androidaiassistant.core.database.entity.ChatAttachmentEntity(
                            attachmentId = it.ui.id,
                            messageId = requestId,
                            sessionId = sessionId,
                            mimeType = it.ui.mimeType,
                            localUri = it.ui.localUri,
                            fileSize = it.ui.fileSize,
                            width = it.ui.width,
                            height = it.ui.height,
                            uploadState = "local",
                            timestamp = System.currentTimeMillis()
                        )
                    }
                )
            }

            // 构建 WebSocket 消息
            val wsMessage = ChatMessageRequest(
                requestId = requestId,
                payload = ChatMessagePayload(
                    messageType = if (prepared.images.isEmpty()) "text" else "image",
                    content = prepared.text.ifBlank { null },
                    images = prepared.images.map {
                        ChatImagePayload(
                            mimeType = it.ui.mimeType,
                            dataBase64 = it.dataBase64,
                            localUri = it.ui.localUri
                        )
                    },
                    timestamp = System.currentTimeMillis()
                )
            )

            // 发送
            val sent = botWebSocketClient.send(gson.toJson(wsMessage))
            if (!sent) {
                pendingRequestIds.remove(requestId)
                chatRepository.markMessageError(requestId, "SEND_FAILED")
                chatRepository.markMessageError("pending_$requestId", "SEND_FAILED")
                _uiState.update { 
                    it.copy(
                        botActivity = if (pendingRequestIds.isEmpty()) BotActivityStatus.IDLE else BotActivityStatus.SENDING,
                        botStage = null,
                        error = "消息发送失败"
                    ) 
                }
                return@launch
            }

            // WS 端成功接受消息 → 立即标记已送达（双勾提示），无需等待 bot 回复
            chatRepository.markUserMessageSent(requestId)

            // 启动超时计时器
            resetStreamingTimeout(requestId, "pending_$requestId")
        }
    }

    /**
     * 删除单条消息（仅本地生效，不影响服务器对话记录）
     */
    fun deleteMessage(messageId: String) {
        viewModelScope.launch {
            // 清理流式/发送中的关联状态，防止消息被流式处理重新写回
            pendingRequestIds.remove(messageId)
            streamingTimeoutJobs.remove(messageId)?.cancel()
            streamingContentCache.remove(messageId)
            receivedFirstChunk.remove(messageId)
            chatRepository.deleteMessage(messageId)
            chatRepository.deleteMessage("pending_$messageId")
        }
    }

    fun retryMessage(messageId: String) {
        viewModelScope.launch {
            val entity = chatRepository.getMessages(sessionId).firstOrNull { it.messageId == messageId || it.messageId == messageId.removePrefix("pending_") }
                ?: return@launch
            if (entity.role != MessageRole.USER) return@launch
            val attachments = chatRepository.getAttachmentsByMessageId(entity.messageId).map {
                ChatAttachmentUi(
                    id = it.attachmentId,
                    localUri = it.localUri,
                    mimeType = it.mimeType,
                    fileSize = it.fileSize,
                    width = it.width,
                    height = it.height
                )
            }
            val uriList = attachments.mapNotNull { runCatching { Uri.parse(it.localUri) }.getOrNull() }
            onPickImages(uriList)
            onInputChange(entity.content)
            sendText()
        }
    }

    /**
     * 手动重连
     */
    fun reconnect() {
        if (_uiState.value.connectionStatus != ConnectionStatus.CONNECTING) {
            val token = authRepository.getAccessToken() ?: ""
            manualReconnectPendingFullSync = true
            _uiState.update { it.copy(connectionStatus = ConnectionStatus.CONNECTING) }
            botWebSocketClient.connect(token)
        }
    }

    override fun onCleared() {
        super.onCleared()
        streamingTimeoutJobs.values.forEach { it.cancel() }
        streamingTimeoutJobs.clear()
        streamingContentCache.clear()
        pendingRequestIds.clear()
        receivedFirstChunk.clear()
    }

    private fun splitBotSegments(content: String): List<String> {
        return content
            .split('\n')
            .map { it.trim() }
            .filter { it.isNotBlank() }
    }

    /**
     * 落库一条 Taki 回复：按换行分条，**统一使用 `${messageId}_$index` 规则**。
     *
     * WS 推送（`handleReplyStream`）、HTTP 兜底（`persistInteractionLocally`）与历史补拉
     * （`applyHistoryItems`）三条路径必须写出完全相同的行，Room 的 REPLACE 才能天然去重；
     * 任何一条写成「整段内容 + 原始 messageId」都会多出一个气泡
     * （互动礼物曾因此出现"整段 + 分条"的双重回复）。
     */
    /**
     * 记录「已由 WS 推送落库」的回复 id：互动走 HTTP 时能据此判断是否还需要兜底写入，
     * 避免 HTTP 兜底用毫秒级新时间戳覆盖 WS 版本（内容相同但会闪一下）。
     */
    private fun markBotReplyDelivered(messageId: String) {
        if (deliveredBotMessageIds.size > 200) deliveredBotMessageIds.clear()
        deliveredBotMessageIds.add(messageId)
    }

    private suspend fun saveBotReply(
        messageId: String,
        content: String,
        contentType: String = ContentType.TEXT,
        modelProvider: String = ModelProvider.DEEPSEEK,
        timestamp: Long = System.currentTimeMillis()
    ) {        val segments = splitBotSegments(content)
        if (segments.isEmpty()) {
            chatRepository.saveBotMessage(
                messageId = messageId,
                sessionId = sessionId,
                content = content,
                isStreaming = false,
                contentType = contentType,
                modelProvider = modelProvider,
                timestamp = timestamp
            )
        } else {
            segments.forEachIndexed { index, segment ->
                chatRepository.saveBotMessage(
                    messageId = "${messageId}_$index",
                    sessionId = sessionId,
                    content = segment,
                    isStreaming = false,
                    contentType = contentType,
                    modelProvider = modelProvider,
                    timestamp = timestamp + index
                )
            }
        }
    }

    private fun syncHistoryFromServer(force: Boolean = false) {
        viewModelScope.launch {
            runCatching {
                chatApi.history(
                    since = if (force) 0L else lastSyncedTimestampMs,
                    limit = 300
                )
            }.onSuccess { response ->
                val items = response.getAsJsonObject("data")
                    ?.getAsJsonArray("items")
                    ?: JsonArray()
                val (inserted, updated) = applyHistoryItems(items)
                android.util.Log.i(
                    "ChatViewModel",
                    "history sync ok: items=${items.size()} inserted=$inserted healed=$updated force=$force"
                )
            }.onFailure { e ->
                // 历史补拉失败不影响实时聊天，但必须可见（此前静默吞噬导致排查困难）
                android.util.Log.w("ChatViewModel", "history sync failed: ${e.message}", e)
                if (force) {
                    manualReconnectPendingFullSync = true
                }
            }
        }
    }

    private suspend fun handleMemoryFactCreated(message: IncomingMessage.MemoryFactCreated) {
        val payload = message.payload
        userFactRepository.upsertAll(
            listOf(
                UserFactEntity(
                    factId = payload.factId,
                    userId = payload.userId,
                    fact = payload.fact,
                    timestamp = payload.timestamp
                )
            )
        )
        if (payload.timestamp > lastSyncedFactTimestampMs) {
            lastSyncedFactTimestampMs = payload.timestamp
        }
    }

    private fun syncMemoryFactsFromServer() {
        viewModelScope.launch {
            runCatching {
                chatApi.memoryFacts(
                    since = lastSyncedFactTimestampMs,
                    limit = 200
                )
            }.onSuccess { response ->
                val items = response.getAsJsonObject("data")
                    ?.getAsJsonArray("items")
                    ?: JsonArray()
                applyMemoryFactItems(items)
            }.onFailure { e ->
                // 记忆补拉失败不影响聊天主流程，但同样记录日志
                android.util.Log.w("ChatViewModel", "memory facts sync failed: ${e.message}", e)
            }
        }
    }

    /**
     * 把服务端历史落库，返回 (新增条数, 修复条数)。
     *
     * 「修复条数」是重点：本地卡在 sending / error 的行会被改回服务端确认的状态，
     * 所以历史补拉成功后，之前那条「发送失败」应该自己消失。
     */
    private suspend fun applyHistoryItems(items: JsonArray): Pair<Int, Int> {
        var inserted = 0
        var updated = 0
        fun tally(outcome: SyncOutcome) {
            when (outcome) {
                SyncOutcome.INSERTED -> inserted++
                SyncOutcome.UPDATED -> updated++
                SyncOutcome.UNCHANGED -> Unit
            }
        }

        for (i in 0 until items.size()) {
            runCatching {
                val item = items[i].asJsonObjectOrNull() ?: return@runCatching
                // 服务端已按 auth.user_id 过滤，客户端不再做 userId 白名单校验
                val messageId = item.getStringOrNull("messageId") ?: return@runCatching
                val role = item.getStringOrNull("role") ?: return@runCatching
                val content = item.getStringOrNull("content") ?: return@runCatching
                if (content.isBlank()) return@runCatching
                val timestamp = item.getLongOrNull("timestamp") ?: return@runCatching

                if (role == "user") {
                    tally(
                        chatRepository.saveExternalMessage(
                            messageId = messageId,
                            sessionId = sessionId,
                            role = MessageRole.USER,
                            content = content,
                            timestamp = timestamp
                        )
                    )
                } else {
                    val segments = splitBotSegments(content)
                    if (segments.isEmpty()) {
                        tally(
                            chatRepository.saveExternalMessage(
                                messageId = messageId,
                                sessionId = sessionId,
                                role = MessageRole.BOT,
                                content = content,
                                timestamp = timestamp
                            )
                        )
                    } else {
                        segments.forEachIndexed { index, segment ->
                            tally(
                                chatRepository.saveExternalMessage(
                                    messageId = "${messageId}_$index",
                                    sessionId = sessionId,
                                    role = MessageRole.BOT,
                                    content = segment,
                                    timestamp = timestamp + index
                                )
                            )
                        }
                    }
                }

                if (timestamp > lastSyncedTimestampMs) {
                    lastSyncedTimestampMs = timestamp
                }
            }
        }
        return inserted to updated
    }

    private suspend fun applyMemoryFactItems(items: JsonArray) {
        val facts = mutableListOf<UserFactEntity>()
        for (i in 0 until items.size()) {
            runCatching {
                val item = items[i].asJsonObjectOrNull() ?: return@runCatching
                val factId = item.getStringOrNull("factId") ?: return@runCatching
                val userId = item.getStringOrNull("userId") ?: return@runCatching
                val fact = item.getStringOrNull("fact") ?: return@runCatching
                val timestamp = item.getLongOrNull("timestamp") ?: return@runCatching
                facts += UserFactEntity(
                    factId = factId,
                    userId = userId,
                    fact = fact,
                    timestamp = timestamp
                )
            }
        }
        if (facts.isNotEmpty()) {
            userFactRepository.upsertAll(facts)
            val latest = facts.maxOf { it.timestamp }
            if (latest > lastSyncedFactTimestampMs) {
                lastSyncedFactTimestampMs = latest
            }
        }
    }

    private fun ChatMessageEntity.toUiModel(attachments: List<ChatAttachmentUi>) = ChatMessageUi(
        id = messageId,
        role = role,
        content = content,
        contentType = contentType,
        modelProvider = modelProvider,
        attachments = attachments,
        timestamp = timestamp,
        status = status,
        errorCode = errorCode,
        isStreaming = status == MessageStatus.STREAMING
    )

    private fun JsonObject.getStringOrNull(name: String): String? {
        val v = get(name) ?: return null
        return if (v.isJsonNull) null else v.asString
    }

    private fun JsonObject.getLongOrNull(name: String): Long? {
        val v = get(name) ?: return null
        return if (v.isJsonNull) null else v.asLong
    }

    private fun com.google.gson.JsonElement.asJsonObjectOrNull(): JsonObject? {
        return if (isJsonObject) asJsonObject else null
    }

    private suspend fun prepareOutgoingPayload(text: String, selectedImages: List<ChatAttachmentUi>): OutgoingPayload? {
        val encodedImages = mutableListOf<OutgoingImage>()
        for (item in selectedImages.take(3)) {
            val uri = runCatching { Uri.parse(item.localUri) }.getOrNull() ?: continue
            val bytes = runCatching { context.contentResolver.openInputStream(uri)?.use { it.readBytes() } }.getOrNull()
            if (bytes == null) {
                _uiState.update { it.copy(error = "读取图片失败") }
                return null
            }
            if (bytes.size > MAX_IMAGE_BYTES) {
                _uiState.update { it.copy(error = MAX_IMAGE_TIPS) }
                return null
            }
            encodedImages += OutgoingImage(
                ui = item,
                dataBase64 = Base64.encodeToString(bytes, Base64.NO_WRAP)
            )
        }
        if (encodedImages.isEmpty() && text.isBlank()) {
            _uiState.update { it.copy(error = "消息内容不能为空") }
            return null
        }
        return OutgoingPayload(text = text, images = encodedImages)
    }

    private fun createUiAttachment(uri: Uri): ChatAttachmentUi? {
        val resolver = context.contentResolver
        val mimeType = resolver.getType(uri) ?: "image/jpeg"
        if (mimeType != "image/jpeg" && mimeType != "image/png") {
            _uiState.update { it.copy(error = "仅支持 JPG/PNG") }
            return null
        }
        val fileSize = resolver.query(uri, arrayOf(android.provider.OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getLong(0) else -1L
        } ?: -1L
        if (fileSize > MAX_IMAGE_BYTES) {
            _uiState.update { it.copy(error = MAX_IMAGE_TIPS) }
            return null
        }
        val bounds = resolver.openInputStream(uri)?.use { input ->
            val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeStream(input, null, options)
            options
        }
        // 复制到应用私有目录：content:// 临时授权与相机临时文件会在会话结束后失效，
        // 消息重建/App 重启后 AsyncImage 将无法再读取，复制后 localUri 永久可读
        val persistedUri = copyToPrivateStorage(uri, mimeType)
            ?: run {
                _uiState.update { it.copy(error = "读取图片失败") }
                return null
            }
        return ChatAttachmentUi(
            id = UUID.randomUUID().toString(),
            localUri = persistedUri.toString(),
            mimeType = mimeType,
            fileSize = if (fileSize < 0L) 0L else fileSize,
            width = bounds?.outWidth?.takeIf { it > 0 },
            height = bounds?.outHeight?.takeIf { it > 0 }
        )
    }

    private fun copyToPrivateStorage(source: Uri, mimeType: String): Uri? {
        return runCatching {
            val dir = java.io.File(context.filesDir, "chat_attachments").apply {
                if (!exists()) mkdirs()
            }
            val ext = if (mimeType == "image/png") "png" else "jpg"
            val target = java.io.File(dir, "${UUID.randomUUID().toString()}.$ext")
            val input = context.contentResolver.openInputStream(source) ?: return null
            input.use { stream ->
                java.io.FileOutputStream(target).use { output -> stream.copyTo(output) }
            }
            Uri.fromFile(target)
        }.getOrNull()
    }
}
