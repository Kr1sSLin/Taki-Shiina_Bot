pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "0.10.0"
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Android_AI_Assistant"
include(
    ":app",
    ":core:common",
    ":core:network",
    ":core:database",
    ":core:push",
    ":core:ui",
    ":feature:auth",
    ":feature:chat",
    ":feature:history",
    ":feature:settings"
)