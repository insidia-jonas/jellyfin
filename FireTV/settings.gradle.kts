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

rootProject.name = "jellyfin-firetv"

include(":core")

fun hasAndroidSdk(): Boolean {
    val localProperties = file("local.properties")
    if (localProperties.exists()) {
        val sdkDir = localProperties.readLines()
            .firstOrNull { it.startsWith("sdk.dir=") }
            ?.substringAfter("=")
            ?.replace("\\\\", "\\")
        if (!sdkDir.isNullOrBlank() && file(sdkDir).exists()) {
            return true
        }
    }
    val env = System.getenv("ANDROID_HOME") ?: System.getenv("ANDROID_SDK_ROOT")
    return !env.isNullOrBlank() && file(env).exists()
}

if (hasAndroidSdk()) {
    include(":app")
}
