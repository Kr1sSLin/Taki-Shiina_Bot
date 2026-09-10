package com.krisslin.androidaiassistant.core.database.di

import android.content.Context
import androidx.room.Room
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import com.krisslin.androidaiassistant.core.database.AppDatabase
import com.krisslin.androidaiassistant.core.database.dao.BotNotificationDao
import com.krisslin.androidaiassistant.core.database.dao.ChatAttachmentDao
import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import com.krisslin.androidaiassistant.core.database.dao.UserFactDao
import com.krisslin.androidaiassistant.core.database.dao.UserProgressDao
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object DatabaseModule {
    private val MIGRATION_1_2 = object : Migration(1, 2) {
        override fun migrate(db: SupportSQLiteDatabase) {
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS `user_facts` (
                    `fact_id` TEXT NOT NULL,
                    `user_id` TEXT NOT NULL,
                    `fact` TEXT NOT NULL,
                    `timestamp` INTEGER NOT NULL,
                    PRIMARY KEY(`fact_id`)
                )
                """.trimIndent()
            )
            db.execSQL(
                "CREATE INDEX IF NOT EXISTS `index_user_facts_user_id_timestamp` ON `user_facts` (`user_id`, `timestamp`)"
            )
        }
    }

    private val MIGRATION_2_3 = object : Migration(2, 3) {
        override fun migrate(db: SupportSQLiteDatabase) {
            db.execSQL("ALTER TABLE chat_messages ADD COLUMN content_type TEXT NOT NULL DEFAULT 'text'")
            db.execSQL("ALTER TABLE chat_messages ADD COLUMN model_provider TEXT NOT NULL DEFAULT 'deepseek'")
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS `chat_attachments` (
                    `attachment_id` TEXT NOT NULL,
                    `message_id` TEXT NOT NULL,
                    `session_id` TEXT NOT NULL,
                    `mime_type` TEXT NOT NULL,
                    `local_uri` TEXT NOT NULL,
                    `file_size` INTEGER NOT NULL,
                    `width` INTEGER,
                    `height` INTEGER,
                    `upload_state` TEXT NOT NULL,
                    `created_at` INTEGER NOT NULL,
                    `timestamp` INTEGER NOT NULL,
                    PRIMARY KEY(`attachment_id`),
                    FOREIGN KEY(`message_id`) REFERENCES `chat_messages`(`message_id`) ON UPDATE NO ACTION ON DELETE CASCADE
                )
                """.trimIndent()
            )
            db.execSQL("CREATE INDEX IF NOT EXISTS `index_chat_attachments_message_id` ON `chat_attachments` (`message_id`)")
            db.execSQL("CREATE INDEX IF NOT EXISTS `index_chat_attachments_session_id_timestamp` ON `chat_attachments` (`session_id`, `timestamp`)")
        }
    }

    private val MIGRATION_3_4 = object : Migration(3, 4) {
        override fun migrate(db: SupportSQLiteDatabase) {
            // PRD §3.1：积分/等级本地缓存表（后端为唯一数据源，本地仅缓存）
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS `user_progress` (
                    `user_id` TEXT NOT NULL,
                    `balance` INTEGER NOT NULL,
                    `level_code` TEXT NOT NULL,
                    `level_name` TEXT NOT NULL,
                    `continuous_days` INTEGER NOT NULL,
                    `next_level_name` TEXT,
                    `next_level_threshold_days` INTEGER,
                    `days_to_next_level` INTEGER,
                    `available_makeup_cards` INTEGER NOT NULL,
                    `gap_days` INTEGER,
                    `break_deadline_date` TEXT,
                    `highest_level_code` TEXT,
                    `updated_at` INTEGER NOT NULL,
                    PRIMARY KEY(`user_id`)
                )
                """.trimIndent()
            )
        }
    }

    @Provides
    @Singleton
    fun provideDatabase(@ApplicationContext context: Context): AppDatabase {
        return Room.databaseBuilder(context, AppDatabase::class.java, "android_ai_assistant.db")
            .addMigrations(MIGRATION_1_2)
            .addMigrations(MIGRATION_2_3)
            .addMigrations(MIGRATION_3_4)
            .build()
    }

    @Provides
    fun provideChatMessageDao(db: AppDatabase): ChatMessageDao = db.chatMessageDao()

    @Provides
    fun provideChatAttachmentDao(db: AppDatabase): ChatAttachmentDao = db.chatAttachmentDao()

    @Provides
    fun provideBotNotificationDao(db: AppDatabase): BotNotificationDao = db.botNotificationDao()

    @Provides
    fun provideUserFactDao(db: AppDatabase): UserFactDao = db.userFactDao()

    @Provides
    fun provideUserProgressDao(db: AppDatabase): UserProgressDao = db.userProgressDao()
}
