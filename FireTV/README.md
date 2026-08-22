# Jellyfin for Fire TV (Web-Oberfläche)

Diese App zeigt **dieselbe vollständige Jellyfin-Web-Oberfläche** wie die [iOS-App](https://github.com/jellyfin/jellyfin-ios): Der Fire TV Stick lädt `jellyfin-web` vom Server in einer WebView und spricht mit dem Gerät über `NativeShell`.

Das ist bewusst **kein** API-only-Client wie [jellyfin-androidtv](https://github.com/jellyfin/jellyfin-androidtv). Bibliothek, Dashboard, Plugins, Themes, Live-TV, Wiedergabeeinstellungen und der HTML5-Player kommen unverändert aus der Web-UI, die der Server ausliefert.

## English

Fire TV client that hosts the full Jellyfin web UI (same architecture as the official iOS app: WebView + NativeShell). It does not rebuild the catalog from REST APIs the way the official Android TV app does.

The web client is forced into **TV layout** (`NativeShell.AppHost.getDefaultLayout() === "tv"`) so D-pad / remote spatial navigation works on a Fire TV Stick.

## Funktionen

- Vollständige `jellyfin-web`-Oberfläche, inklusive Login, Home, Bibliotheken, Dashboard und Player
- TV-Layout und Fernbedienungs-Tasten (Zurück, Play/Pause, Spulen)
- Server-Suche im LAN (UDP 7359, `Who is JellyfinServer?`) oder manuelle Adresse
- HTTP im Heimnetz und optional selbstsignierte HTTPS-Zertifikate
- Menü-Taste: Neu laden, Server wechseln, Beenden
- Leanback-Launcher / Fire TV Home (Banner + `LEANBACK_LAUNCHER`)

## Bauen

Voraussetzungen: JDK 17, Android SDK (API 35), Android Studio oder Command-Line Tools.

```bash
cd FireTV
# SDK-Pfad, falls nicht über ANDROID_HOME gesetzt:
echo "sdk.dir=/path/to/Android/Sdk" > local.properties

./gradlew :core:test
./gradlew :app:assembleDebug
```

Die Debug-APK liegt unter `FireTV/app/build/outputs/apk/debug/app-debug.apk`.

Ohne Android-SDK wird nur das JVM-Modul `:core` eingebunden; `./gradlew :core:test` bleibt lauffähig.

## Installation auf dem Fire TV Stick

1. Entwickleroptionen und ADB-Debugging auf dem Stick aktivieren.
2. Stick und Rechner ins selbe Netz bringen.
3. Sideloaden:

```bash
adb connect <FIRE-TV-IP>
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n org.jellyfin.firetvweb/org.jellyfin.firetv.connect.ConnectActivity
```

Die App erscheint im Fire-TV-Startmenü unter **Jellyfin**. Beim ersten Start Serveradresse eingeben (z. B. `192.168.1.10`) oder **Im Netzwerk suchen**. Danach läuft die komplette Web-UI, analog zur iOS-App.

Paketname: `org.jellyfin.firetvweb` — parallel zur offiziellen Android-TV-App (`org.jellyfin.androidtv`) installierbar.

## Hinweise zur Wiedergabe

Video läuft über den **HTML5-Player von jellyfin-web** in der Amazon-WebView, nicht über den nativen ExoPlayer der API-App. Der Server transcodiert zu HLS/H.264/AAC, wenn Direct Play in der WebView nicht möglich ist (typisch für viele MKVs). Das entspricht dem Verhalten der iOS-/Android-Web-Shells ohne Native-Player-Plugin.
