pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
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