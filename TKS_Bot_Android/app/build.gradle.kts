plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.kapt)
    alias(libs.plugins.hilt.android)
}

/**
 * 发布签名（构建约定）。
 *
 * 密钥信息一律由环境变量注入，**不进仓库**（见仓库根 `scripts/release.sh`）：
 *   TKS_ANDROID_KEYSTORE      密钥库绝对路径（如 /home/administrator/1.jks）
 *   TKS_ANDROID_KEYSTORE_PASS storePassword 与 keyPassword
 *   TKS_ANDROID_KEY_ALIAS     密钥别名（默认 "1"）
 *
 * 缺少密钥变量时不创建 signingConfig，release 变体退化为未签名产物
 * （app-release-unsigned.apk）—— 这样在没有密钥的机器上依然能跑通构建。
 */
val releaseKeystorePath: String? = System.getenv("TKS_ANDROID_KEYSTORE")
val releaseKeystorePass: String? = System.getenv("TKS_ANDROID_KEYSTORE_PASS")
val releaseKeyAlias: String = System.getenv("TKS_ANDROID_KEY_ALIAS") ?: "1"
val hasReleaseSigning: Boolean =
    !releaseKeystorePath.isNullOrBlank() && !releaseKeystorePass.isNullOrBlank()

android {
    namespace = "com.krisslin.androidaiassistant"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.krisslin.androidaiassistant"
        minSdk = 26
        targetSdk = 34
        versionCode = 3
        versionName = "1.2.1"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables.useSupportLibrary = true
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = file(releaseKeystorePath!!)
                storePassword = releaseKeystorePass
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeystorePass
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // 有密钥才挂签名；否则产出 app-release-unsigned.apk
            if (hasReleaseSigning) signingConfig = signingConfigs.getByName("release")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
    }

    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

dependencies {
    implementation(project(":core:ui"))
    implementation(project(":core:common"))
    implementation(project(":core:network"))
    implementation(project(":core:database"))
    implementation(project(":core:push"))
    implementation(libs.gson)
    implementation(project(":feature:auth"))
    implementation(project(":feature:chat"))
    implementation(project(":feature:history"))
    implementation(project(":feature:settings"))
    implementation(project(":feature:interaction"))
    implementation(project(":feature:profile"))

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.biometric)
    implementation(libs.androidx.work.runtime.ktx)

    implementation(libs.hilt.android)
    kapt(libs.hilt.compiler)
    implementation(libs.androidx.hilt.navigation.compose)

    debugImplementation(libs.androidx.compose.ui.tooling)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
}

kapt {
    correctErrorTypes = true
}
