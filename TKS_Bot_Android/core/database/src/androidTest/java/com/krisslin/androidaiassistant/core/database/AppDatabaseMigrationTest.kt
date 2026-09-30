package com.krisslin.androidaiassistant.core.database

import android.content.Context
import androidx.room.Room
import androidx.sqlite.db.SupportSQLiteDatabase
import androidx.sqlite.db.SupportSQLiteOpenHelper
import androidx.sqlite.db.framework.FrameworkSQLiteOpenHelperFactory
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.krisslin.androidaiassistant.core.database.di.DatabaseModule
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AppDatabaseMigrationTest {
    private val databaseName = "app-database-migration-test"
    private val context = ApplicationProvider.getApplicationContext<Context>()

    @After
    fun cleanUp() {
        context.deleteDatabase(databaseName)
    }

    @Test
    fun migrateAll_1To5() {
        createVersionOneFixture()
        openAndVerifyMigration(hasAttachment = false)
    }

    @Test
    fun migrateFreshInstall_4To5_preservesAttachmentAndForeignKey() {
        createVersionFourFixture(freshInstall = true)
        openAndVerifyMigration(hasAttachment = true)
    }

    @Test
    fun migratePreviouslyUpgraded_4To5_preservesAttachmentAndForeignKey() {
        createVersionFourFixture(freshInstall = false)
        openAndVerifyMigration(hasAttachment = true)
    }

    private fun openAndVerifyMigration(hasAttachment: Boolean) {
        val migrated = Room.databaseBuilder(context, AppDatabase::class.java, databaseName)
            .addMigrations(
                DatabaseModule.MIGRATION_1_2,
                DatabaseModule.MIGRATION_2_3,
                DatabaseModule.MIGRATION_3_4,
                DatabaseModule.MIGRATION_4_5
            )
            .build()

        try {
        val db = migrated.openHelper.writableDatabase
        assertEquals(5, db.version)
        db.query(
            "SELECT content, content_type, model_provider FROM chat_messages WHERE message_id = 'request-1'"
        ).use { cursor ->
            assertTrue(cursor.moveToFirst())
            assertEquals("hello", cursor.getString(0))
            assertEquals("text", cursor.getString(1))
            assertEquals("deepseek", cursor.getString(2))
        }
        db.query("PRAGMA foreign_key_check").use { assertEquals(0, it.count) }
        if (hasAttachment) {
            db.query("SELECT message_id, local_uri, file_size FROM chat_attachments WHERE attachment_id = 'attachment-1'").use {
                assertTrue(it.moveToFirst())
                assertEquals("request-1", it.getString(0))
                assertEquals("/private/image.jpg", it.getString(1))
                assertEquals(123L, it.getLong(2))
            }
            db.execSQL("DELETE FROM chat_messages WHERE message_id = 'request-1'")
            db.query("SELECT * FROM chat_attachments").use { assertEquals(0, it.count) }
        }
        } finally {
            migrated.close()
        }
    }

    private fun createVersionFourFixture(freshInstall: Boolean) {
        createVersionOneFixture()
        val helper = FrameworkSQLiteOpenHelperFactory().create(
            SupportSQLiteOpenHelper.Configuration.builder(context)
                .name(databaseName)
                .callback(object : SupportSQLiteOpenHelper.Callback(4) {
                    override fun onConfigure(db: SupportSQLiteDatabase) {
                        db.setForeignKeyConstraintsEnabled(true)
                    }
                    override fun onCreate(db: SupportSQLiteDatabase) = error("Expected v1 fixture")
                    override fun onUpgrade(db: SupportSQLiteDatabase, oldVersion: Int, newVersion: Int) {
                        DatabaseModule.MIGRATION_1_2.migrate(db)
                        DatabaseModule.MIGRATION_2_3.migrate(db)
                        DatabaseModule.MIGRATION_3_4.migrate(db)
                    }
                }).build()
        )
        try {
            val db = helper.writableDatabase
            if (freshInstall) {
                // The released v4 entity created this table without SQL defaults or an index.
                // Attachments are inserted afterwards; none can be lost while constructing this fixture.
                db.execSQL("DROP TABLE chat_messages")
                db.execSQL(
                    """CREATE TABLE chat_messages (
                        message_id TEXT NOT NULL PRIMARY KEY, session_id TEXT NOT NULL,
                        role TEXT NOT NULL, message_type TEXT NOT NULL, content_type TEXT NOT NULL,
                        model_provider TEXT NOT NULL, content TEXT NOT NULL, image_url TEXT,
                        weather_attached INTEGER NOT NULL, status TEXT NOT NULL,
                        timestamp INTEGER NOT NULL, error_code TEXT
                    )""".trimIndent()
                )
                db.execSQL("INSERT INTO chat_messages VALUES ('request-1', 'default_session', 'user', 'text', 'text', 'deepseek', 'hello', NULL, 0, 'error', 1, 'TIMEOUT')")
            }
            db.execSQL("INSERT INTO chat_attachments VALUES ('attachment-1', 'request-1', 'default_session', 'image/jpeg', '/private/image.jpg', 123, 20, 30, 'ready', 1, 1)")
            // A installed Room database carries its historical identity, which v5 must replace.
            db.execSQL("CREATE TABLE room_master_table (id INTEGER PRIMARY KEY, identity_hash TEXT)")
            db.execSQL("INSERT INTO room_master_table VALUES (42, 'historical-v4-identity')")
        } finally {
            helper.close()
        }
    }

    /**
     * Historical schemas 1-3 were never exported. Build a real version-1 SQLite fixture without
     * MigrationTestHelper (which requires the missing v1 JSON), then let Room migrate and validate v5.
     */
    private fun createVersionOneFixture() {
        context.openOrCreateDatabase(databaseName, Context.MODE_PRIVATE, null).use { db ->
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS `chat_messages` (
                    `message_id` TEXT NOT NULL,
                    `session_id` TEXT NOT NULL,
                    `role` TEXT NOT NULL,
                    `message_type` TEXT NOT NULL,
                    `content` TEXT NOT NULL,
                    `image_url` TEXT,
                    `weather_attached` INTEGER NOT NULL,
                    `status` TEXT NOT NULL,
                    `timestamp` INTEGER NOT NULL,
                    `error_code` TEXT,
                    PRIMARY KEY(`message_id`)
                )
                """.trimIndent()
            )
            db.execSQL(
                "CREATE INDEX IF NOT EXISTS `index_chat_messages_session_id_timestamp` " +
                    "ON `chat_messages` (`session_id`, `timestamp`)"
            )
            db.execSQL(
                """
                CREATE TABLE IF NOT EXISTS `bot_notifications` (
                    `notification_id` TEXT NOT NULL,
                    `error_code` TEXT NOT NULL,
                    `message` TEXT NOT NULL,
                    `is_read` INTEGER NOT NULL,
                    `timestamp` INTEGER NOT NULL,
                    PRIMARY KEY(`notification_id`)
                )
                """.trimIndent()
            )
            db.execSQL(
                "INSERT INTO chat_messages " +
                    "(message_id, session_id, role, message_type, content, image_url, weather_attached, status, timestamp, error_code) " +
                    "VALUES ('request-1', 'default_session', 'user', 'text', 'hello', NULL, 0, 'error', 1, 'TIMEOUT')"
            )
            db.version = 1
        }
    }
}
