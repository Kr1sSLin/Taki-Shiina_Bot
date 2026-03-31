package com.krisslin.androidaiassistant.core.network.di

import com.krisslin.androidaiassistant.core.network.BuildConfig
import com.krisslin.androidaiassistant.core.network.api.AuthApi
import com.krisslin.androidaiassistant.core.network.api.ChatApi
import com.krisslin.androidaiassistant.core.network.auth.AuthInterceptor
import com.krisslin.androidaiassistant.core.network.auth.TokenAuthenticator
import com.krisslin.androidaiassistant.core.network.ws.BotWebSocketClient
import com.krisslin.androidaiassistant.core.network.ws.OkHttpBotWebSocketClient
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import com.google.gson.Gson
import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit
import javax.inject.Qualifier
import javax.inject.Singleton

@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class AuthOkHttpClient

@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class AuthRetrofit

@Module
@InstallIn(SingletonComponent::class)
object NetworkModule {

    /**
     * 不带认证拦截器的 OkHttpClient，用于 AuthApi（避免循环依赖）
     */
    @Provides
    @Singleton
    @AuthOkHttpClient
    fun provideAuthOkHttpClient(): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .build()

    /**
     * 带认证拦截器的 OkHttpClient，用于其他 API
     */
    @Provides
    @Singleton
    fun provideOkHttpClient(
        authInterceptor: AuthInterceptor,
        tokenAuthenticator: TokenAuthenticator
    ): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(30, TimeUnit.SECONDS)
        .addInterceptor(authInterceptor)
        .authenticator(tokenAuthenticator)
        .build()

    @Provides
    @Singleton
    fun provideGson(): Gson = Gson()

    /**
     * 用于 AuthApi 的 Retrofit（不带认证拦截器）
     */
    @Provides
    @Singleton
    @AuthRetrofit
    fun provideAuthRetrofit(@AuthOkHttpClient client: OkHttpClient, gson: Gson): Retrofit {
        return Retrofit.Builder()
            .baseUrl(BuildConfig.API_BASE_URL)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create(gson))
            .build()
    }

    /**
     * 用于其他 API 的 Retrofit（带认证拦截器）
     */
    @Provides
    @Singleton
    fun provideRetrofit(client: OkHttpClient, gson: Gson): Retrofit {
        return Retrofit.Builder()
            .baseUrl(BuildConfig.API_BASE_URL)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create(gson))
            .build()
    }

    @Provides
    @Singleton
    fun provideAuthApi(@AuthRetrofit retrofit: Retrofit): AuthApi = retrofit.create(AuthApi::class.java)

    @Provides
    @Singleton
    fun provideChatApi(retrofit: Retrofit): ChatApi = retrofit.create(ChatApi::class.java)

    @Provides
    @Singleton
    fun provideBotWebSocketClient(
        impl: OkHttpBotWebSocketClient
    ): BotWebSocketClient = impl
}
