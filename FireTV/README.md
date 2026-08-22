# Jellyfin for Fire TV (Web-Oberfläche)

Diese App zeigt **dieselbe vollständige Jellyfin-Web-Oberfläche** wie die [iOS-App](https://github.com/jellyfin/jellyfin-ios): Der Fire TV Stick lädt `jellyfin-web` vom Server in einer WebView und spricht mit dem Gerät über `NativeShell`.

Das ist bewusst **kein** API-only-Client wie [jellyfin-androidtv](https://github.com/jellyfin/jellyfin-androidtv). Bibliothek, Dashboard, Plugins, Themes und Live-TV kommen aus der Web-UI. **Film- und Serienwiedergabe** läuft über ExoPlayer (wie auf Android TV üblich), damit das Auswählen einer Fassung den Stick nicht einfriert. Die Oberfläche wird auf **1920×1080 CSS-Pixel** gelegt, unabhängig von der WebView-Dichte.

## English

Fire TV client that hosts the full Jellyfin web UI (same architecture as the official iOS app: WebView + NativeShell). Video playback uses ExoPlayer. The WebView is forced to a 1920×1080 viewport so the TV layout is not magnified by device density.

The web client is forced into **TV layout** (`NativeShell.AppHost.getDefaultLayout() === "tv"`) so D-pad / remote spatial navigation works on a Fire TV Stick.

## Funktionen

- Vollständige `jellyfin-web`-Oberfläche, inklusive Login, Home, Bibliotheken und Dashboard
- TV-Layout in nativer 1080p-Skalierung (kein aufgeblasenes Handy-Layout)
- Nativer ExoPlayer beim Abspielen / Auswählen einer Fassung (Release)
- Offizielles Jellyfin-Logo (jellyfin-ux)
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

Die Oberfläche bleibt jellyfin-web. Sobald du Play oder eine Fassung/Release wählst, übernimmt **ExoPlayer** (Direct Play für MKV/MP4 wo möglich, sonst HLS-Transcode). Zurück auf der Fernbedienung beendet die Wiedergabe und kehrt in die Web-UI zurück.
