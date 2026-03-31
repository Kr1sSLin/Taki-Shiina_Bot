package com.krisslin.androidaiassistant.core.database.di

import android.content.Context
import androidx.room.Room
import com.krisslin.androidaiassistant.core.database.AppDatabase
import com.krisslin.androidaiassistant.core.database.dao.BotNotificationDao
import com.krisslin.androidaiassistant.core.database.dao.ChatMessageDao
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object DatabaseModule {
    @Provides
    @Singleton
    fun provideDatabase(@ApplicationContext context: Context): AppDatabase {
        return Room.databaseBuilder(context, AppDatabase::class.java, "android_ai_assistant.db").build()
    }

    @Provides
    fun provideChatMessageDao(db: AppDatabase): ChatMessageDao = db.chatMessageDao()

    @Provides
    fun provideBotNotificationDao(db: AppDatabase): BotNotificationDao = db.botNotificationDao()
}