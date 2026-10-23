package com.krisslin.androidaiassistant.feature.chat

import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.util.Base64
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.viewmodel.compose.LocalViewModelStoreOwner
import androidx.compose.runtime.CompositionLocalProvider
import androidx.room.Room
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.work.WorkManager
import com.google.gson.Gson
import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.krisslin.androidaiassistant.core.database.AppDatabase
import com.krisslin.androidaiassistant.core.database.repository.ChatRepository
import com.krisslin.androidaiassistant.core.database.repository.UserFactRepository
import com.krisslin.androidaiassistant.core.database.repository.BotNotificationRepository
import com.krisslin.androidaiassistant.core.database.repository.UserProgressRepository
import com.krisslin.androidaiassistant.core.network.BuildConfig
import com.krisslin.androidaiassistant.core.network.api.AuthApi
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.api.GamificationApi
import com.krisslin.androidaiassistant.core.network.api.MemoryPermissionSync
import com.krisslin.androidaiassistant.core.network.auth.AuthRepository
import com.krisslin.androidaiassistant.core.network.auth.TokenManager
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.WebSocketEvent
import com.krisslin.androidaiassistant.core.push.ReminderScheduler
import com.krisslin.androidaiassistant.core.ui.theme.AppTheme
import com.krisslin.androidaiassistant.core.ui.theme.ThemeMode
import com.krisslin.androidaiassistant.feature.interaction.InteractionMenuViewModel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import retrofit2.HttpException
import retrofit2.Response
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.lang.reflect.Proxy
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class ChatTimelineSynchronizerTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val databaseName = "p2-history-${UUID.randomUUID()}.db"
    private val session = "default_session"
    private lateinit var database: AppDatabase
    private lateinit var repository: ChatRepository
    private lateinit var tokens: TokenManager
    private lateinit var auth: AuthRepository
    private lateinit var api: ChatApi
    private var page: (String?) -> JsonObject = { error("未安排历史页") }
    private var image: () -> Response<okhttp3.ResponseBody> = { error("未安排图片") }
    private val requested = mutableListOf<String?>()
    private val imageFiles = mutableListOf<File>()

    @Before
    fun setUp() {
        openDatabase()
        tokens = TokenManager(context)
        setOwner("owner-a")
        val authApi = Proxy.newProxyInstance(AuthApi::class.java.classLoader, arrayOf(AuthApi::class.java)) { _, _, _ ->
            error("历史同步不应外呼认证")
        } as AuthApi
        auth = AuthRepository(authApi, tokens)
        api = Proxy.newProxyInstance(ChatApi::class.java.classLoader, arrayOf(ChatApi::class.java)) { _, method, args ->
            when (method.name) {
                "history" -> (args[3] as String?).let { requested += it; page(it) }
                "historyImage" -> image()
                "memoryFacts" -> JsonObject().apply {
                    addProperty("code", 0)
                    add("data", JsonObject().apply {
                        add("items", JsonArray()); addProperty("nextCursor", 0); addProperty("hasMore", false)
                    })
                }
                else -> error("历史同步出现无关外呼：${method.name}")
            }
        } as ChatApi
    }

    @After
    fun tearDown() {
        tokens.clearTokens()
        database.close()
        context.deleteDatabase(databaseName)
        imageFiles.forEach { it.delete() }
    }

    private fun openDatabase() {
        database = Room.databaseBuilder(context, AppDatabase::class.java, databaseName).build()
        repository = ChatRepository(database.chatMessageDao(), database.chatAttachmentDao(), database)
    }

    private fun synchronizer() = ChatTimelineSynchronizer(api, repository,
        UserFactRepository(database, database.userFactDao()), auth, session,
        ChatImageAttachmentManager(context, repository, session, api))

    private fun scope(owner: String = "owner-a") = "${BuildConfig.API_BASE_URL.trimEnd('/')}|$owner"

    private fun setOwner(owner: String) {
        val payload = Base64.encodeToString("""{"sub":"$owner"}""".toByteArray(), Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        tokens.saveTokens("synthetic.$payload.synthetic", "synthetic-refresh")
    }

    private fun item(id: String, content: String = id, source: String = "sql", timestamp: Long = 7L) = JsonObject().apply {
        addProperty("messageId", id); addProperty("role", "user"); addProperty("userId", "owner-a")
        addProperty("content", content); addProperty("timestamp", timestamp); addProperty("historySource", source)
    }

    private fun response(items: List<JsonObject> = emptyList(), next: String = "done", more: Boolean = false,
                         versioned: Boolean = true, blocked: Boolean = false) = JsonObject().apply {
        addProperty("code", 0)
        add("data", JsonObject().apply {
            add("items", JsonArray().apply { items.forEach { add(it) } })
            if (versioned) {
                addProperty("historySyncVersion", 1); addProperty("datasetGeneration", "synthetic-g1")
                addProperty("nextCursor", next); addProperty("hasMore", more)
                addProperty("blocked", blocked); addProperty("pollAfterMs", 1000)
            }
        })
    }

    @Test
    fun pagesResumeAfterReopenAndResetDoesNotOverwriteSql() = runBlocking {
        page = { cursor ->
            if (cursor == "start") response(listOf(item("a", "SQL authority")), "page-a", true)
            else throw IOException("合成断线")
        }
        assertFalse(synchronizer().syncHistory(false).success)
        assertEquals("page-a", repository.historyState(scope()))
        assertEquals("SQL authority", repository.getMessage("a")?.content)
        database.close(); openDatabase(); requested.clear()
        page = { response(listOf(item("b"))) }
        assertTrue(synchronizer().syncHistory(false).success)
        assertEquals(listOf("page-a"), requested)
        val records = repository.getMessages(session, Int.MAX_VALUE)
        assertEquals(listOf("a", "b"), records.map { it.messageId })
        var reset = true
        page = {
            if (reset) { reset = false; throw HttpException(Response.error<JsonObject>(409,
                """{"code":40910,"data":{"reason":"cursor_reset_required"}}""".toResponseBody("application/json".toMediaType()))) }
            response(listOf(item("a", "old projection", "legacy")), "reset-done")
        }
        assertTrue(synchronizer().syncHistory(false).success)
        assertEquals("SQL authority", repository.getMessage("a")?.content)
        assertEquals("reset-done", repository.historyState(scope()))
    }

    @Test
    fun checkpointFailureRollsBackMessagesAndOldServerDoesNotAdvanceIt() = runBlocking {
        database.openHelper.writableDatabase.execSQL("""CREATE TRIGGER p2_checkpoint_failure BEFORE INSERT ON history_sync_state
            WHEN NEW.`key`='${scope()}' BEGIN SELECT RAISE(ABORT,'synthetic checkpoint interruption'); END""")
        page = { response(listOf(item("rolled-back"))) }
        assertFalse(synchronizer().syncHistory(false).success)
        assertNull(repository.getMessage("rolled-back"))
        assertNull(repository.historyState(scope()))
        database.openHelper.writableDatabase.execSQL("DROP TRIGGER p2_checkpoint_failure")
        page = { response(listOf(item("retained"))) }
        assertTrue(synchronizer().syncHistory(false).success)
        page = { response(listOf(item("limited")), versioned = false) }
        val limited = synchronizer().syncHistory(false)
        assertFalse(limited.success)
        assertEquals("当前服务端仅支持有限历史同步", limited.notice)
        assertEquals("done", repository.historyState(scope()))
        assertNotNull(repository.getMessage("retained"))
    }

    @Test
    fun accountChangeRejectsPageAndDoesNotConsumePreviousImageQueue() = runBlocking {
        page = { setOwner("owner-b"); response(listOf(item("wrong-account"))) }
        assertFalse(synchronizer().syncHistory(false).success)
        assertNull(repository.getMessage("wrong-account"))
        assertNull(repository.historyState(scope()))
        setOwner("owner-a")
        val source = item("image-message", "[图片]").apply { addProperty("requestId", "synthetic-image-source"); addProperty("imageCount", 1) }
        var downloads = 0
        val bytes = ByteArrayOutputStream().use { output ->
            Bitmap.createBitmap(2, 2, Bitmap.Config.ARGB_8888).apply {
                compress(Bitmap.CompressFormat.PNG, 100, output); recycle()
            }
            output.toByteArray()
        }
        page = { response(listOf(source)) }
        image = { downloads++; setOwner("owner-b"); Response.success(bytes.toResponseBody("image/png".toMediaType())) }
        synchronizer().syncHistory(false)
        assertEquals(1, repository.getPendingHistoryAttachments(session).size)
        page = { response() }
        assertTrue(synchronizer().syncHistory(false).success)
        assertEquals(1, downloads)
        setOwner("owner-a")
        image = { downloads++; Response.success(bytes.toResponseBody("image/png".toMediaType())) }
        assertTrue(synchronizer().syncHistory(false).success)
        assertEquals(2, downloads)
        assertTrue(repository.getPendingHistoryAttachments(session).isEmpty())
        val attachment = repository.getAttachmentsByMessageId("image-message").single()
        val file = File(Uri.parse(attachment.localUri).path!!).also { imageFiles += it }
        assertArrayEquals(bytes, file.readBytes())
    }

    @Test
    fun scans10050EqualTimestampRowsAndWaitsOnBlockedPage() = runBlocking {
        var blocked = true
        page = { cursor ->
            if (blocked) { blocked = false; response(next = "0", more = true, blocked = true) }
            else {
                val start = cursor!!.toInt()
                val end = minOf(start + 300, 10050)
                response((start until end).map { item("row-${it.toString().padStart(5, '0')}") }, end.toString(), end < 10050)
            }
        }
        val began = android.os.SystemClock.elapsedRealtime()
        assertTrue(synchronizer().syncHistory(false).success)
        assertTrue(android.os.SystemClock.elapsedRealtime() - began >= 1000)
        assertEquals(35, requested.size)
        val records = repository.getMessages(session, Int.MAX_VALUE)
        assertEquals(10050, records.size)
        assertEquals(10050, records.map { it.messageId }.toSet().size)
        assertTrue(records.zipWithNext().all { (a, b) -> a.messageId < b.messageId })
        assertEquals("10050", repository.historyState(scope()))
    }

    @Test
    fun chatScreenConsumesReconnectedHistoryAndRestoredImage() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val events = MutableSharedFlow<WebSocketEvent>(extraBufferCapacity = 4)
        val socket = object : BotWebSocketClient {
            override val events = events
            override fun connect(token: String) = Unit
            override fun disconnect() = Unit
            override fun send(rawJson: String) = false
        }
        val gifts = Proxy.newProxyInstance(GamificationApi::class.java.classLoader, arrayOf(GamificationApi::class.java)) { _, _, _ ->
            throw IOException("GUI 历史测试不外呼积分")
        } as GamificationApi
        val bytes = ByteArrayOutputStream().use { output ->
            Bitmap.createBitmap(160, 100, Bitmap.Config.ARGB_8888).apply {
                eraseColor(android.graphics.Color.rgb(80, 140, 190))
                compress(Bitmap.CompressFormat.PNG, 100, output); recycle()
            }
            output.toByteArray()
        }
        val source = item("ui-image", "恢复后的原图", timestamp = 1700000000001L).apply {
            addProperty("requestId", "ui-image-source"); addProperty("imageCount", 1)
        }
        val reply = item("ui-reply", "历史已恢复\n同一条回复不会重复", timestamp = 1700000000002L).apply { addProperty("role", "bot") }
        var interrupt = true
        page = { cursor ->
            if (cursor == "start") response(listOf(item("ui-old", "较早保留的历史", timestamp = 1700000000000L)), "ui-page-a", true)
            else if (interrupt) throw IOException("合成断线")
            else response(listOf(source, reply), "ui-done")
        }
        image = { Response.success(bytes.toResponseBody("image/png".toMediaType())) }
        lateinit var model: ChatViewModel
        ActivityScenario.launch(HistoryPreviewActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val facts = UserFactRepository(database, database.userFactDao())
                model = ViewModelProvider(activity, object : ViewModelProvider.Factory {
                    @Suppress("UNCHECKED_CAST")
                    override fun <T : ViewModel> create(modelClass: Class<T>): T = ChatViewModel(context, api, gifts, repository,
                        facts, BotNotificationRepository(database.botNotificationDao()), UserProgressRepository(database.userProgressDao()),
                        auth, MemoryPermissionSync(api, facts, auth), socket, ReminderScheduler(WorkManager.getInstance(context)), Gson()) as T
                })[ChatViewModel::class.java]
                ViewModelProvider(activity.auxiliaryModels, object : ViewModelProvider.Factory {
                    @Suppress("UNCHECKED_CAST")
                    override fun <T : ViewModel> create(modelClass: Class<T>): T = InteractionMenuViewModel(gifts) as T
                })[InteractionMenuViewModel::class.java]
                activity.setContent {
                    CompositionLocalProvider(LocalViewModelStoreOwner provides activity.auxiliaryModels) {
                        AppTheme(themeMode = ThemeMode.LIGHT, darkThemeDetected = false) { ChatRoute(viewModel = model) }
                    }
                }
            }
            withTimeout(15000) { while (model.uiState.value.messages.size != 1 || repository.historyState(scope()) != "ui-page-a") delay(50) }
            interrupt = false
            events.emit(WebSocketEvent.Disconnected)
            events.emit(WebSocketEvent.Connected)
            withTimeout(15000) { while (model.uiState.value.messages.size != 4 || model.uiState.value.messages.none { it.id == "ui-image" && it.attachments.size == 1 }) delay(50) }
            val ids = model.uiState.value.messages.map { it.id }
            assertEquals(listOf("ui-old", "ui-image", "ui-reply_0", "ui-reply_1"), ids)
            assertEquals("ui-done", repository.historyState(scope()))
            val attachment = repository.getAttachmentsByMessageId("ui-image").single()
            imageFiles += File(Uri.parse(attachment.localUri).path!!)
            instrumentation.waitForIdleSync()
            delay(1200)
            val screenshot = File(context.getExternalFilesDir(null), "p2-history-reconnected.png")
            instrumentation.uiAutomation.takeScreenshot().apply {
                screenshot.outputStream().use { compress(Bitmap.CompressFormat.PNG, 100, it) }
                recycle()
            }
            assertTrue(screenshot.length() > 0)
        }
    }
}

class HistoryPreviewActivity : ComponentActivity() {
    val auxiliaryModels = object : ViewModelStoreOwner { override val viewModelStore = ViewModelStore() }
    override fun onDestroy() {
        auxiliaryModels.viewModelStore.clear()
        super.onDestroy()
    }
}
