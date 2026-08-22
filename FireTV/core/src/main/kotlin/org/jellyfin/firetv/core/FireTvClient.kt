package org.jellyfin.firetv.core

/**
 * Shared constants for the Fire TV web-shell client.
 */
object FireTvClient {
    const val APP_NAME: String = "Jellyfin Fire TV"
    const val APP_VERSION: String = "1.3.1"
    const val DEFAULT_LAYOUT: String = "tv"
    const val DEFAULT_HTTP_PORT: Int = 8096
    const val DISCOVERY_PORT: Int = 7359
    const val DISCOVERY_MESSAGE: String = "Who is JellyfinServer?"
    const val PUBLIC_INFO_PATH: String = "/System/Info/Public"
    const val NATIVE_SHELL_SCRIPT: String = "/native/nativeshell.js"
}
