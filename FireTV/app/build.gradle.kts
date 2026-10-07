plugins {
    id("com.android.application")
    kotlin("android")
}

val previewBuild = providers.gradleProperty("firetvPreview").orNull == "true"

// The WebView replaces the server's Live TV script, so ship the shared health UI
// as a native asset too. Generate it from the server source to keep both in sync.
val channelHealthAssets = layout.buildDirectory.dir("generated/channelHealthAssets")
val generateChannelHealthAssets by tasks.registering(Sync::class) {
    from(rootProject.file("../src/Jellyfin.LiveTv/Web/channel-health.js"))
    into(channelHealthAssets.map { it.dir("native") })
    rename { "channelHealth.js" }
}

android {
    namespace = "org.jellyfin.firetv"
    compileSdk = 35

    defaultConfig {
        applicationId = if (previewBuild) "org.jellyfin.firetvweb.preview" else "org.jellyfin.firetvweb"
        minSdk = 25
        targetSdk = 34
        versionCode = 35
        versionName = "2.5.5"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        isCoreLibraryDesugaringEnabled = true
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        viewBinding = true
        buildConfig = true
    }

    sourceSets.getByName("main").assets.srcDir(channelHealthAssets)
}

tasks.named("preBuild").configure { dependsOn(generateChannelHealthAssets) }

dependencies {
    coreLibraryDesugaring("com.android.tools:desugar_jdk_libs:2.0.3")
    implementation(project(":core"))
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.constraintlayout:constraintlayout:2.2.0")
    implementation("androidx.webkit:webkit:1.12.1")
    implementation("androidx.activity:activity-ktx:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("androidx.media:media:1.7.0")
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-exoplayer-hls:1.4.1")
    implementation("androidx.media3:media3-datasource:1.4.1")
    implementation("androidx.media3:media3-ui:1.4.1")
}
