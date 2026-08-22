# Jellyfin for Fire TV (Web-Oberfläche)

Diese App zeigt **dieselbe vollständige Jellyfin-Web-Oberfläche** wie die [iOS-App](https://github.com/jellyfin/jellyfin-ios): Der Fire TV Stick lädt `jellyfin-web` vom Server in einer WebView und spricht mit dem Gerät über `NativeShell`.

Das ist bewusst **kein** API-only-Client wie [jellyfin-androidtv](https://github.com/jellyfin/jellyfin-androidtv). Bibliothek, Dashboard, Plugins, Themes und Live-TV kommen aus der Web-UI. **Film- und Serienwiedergabe** läuft über ExoPlayer, **Downloads** über den Android DownloadManager. Die Oberfläche wird auf **1920×1080 CSS-Pixel** gelegt.

## English

Fire TV client that hosts the full Jellyfin web UI (same architecture as the official iOS app: WebView + NativeShell). Video playback uses ExoPlayer so selecting a movie cannot freeze Amazon WebView. Downloads use Android DownloadManager. The WebView is forced to a 1920×1080 viewport.

## Funktionen

- Vollständige `jellyfin-web`-Oberfläche, inklusive Login, Home, Bibliotheken und Dashboard
- TV-Layout in nativer 1080p-Skalierung
- Nativer ExoPlayer (ES-Modul `/native/ExoPlayerPlugin.js`, wie jellyfin-android) — HTML5-Video im Amazon-WebView ist deaktiviert
- Downloads über die Web-UI (`filedownload`) in den Ordner Downloads
- Kleinere Poster, keine doppelte NativeShell-Injektion, kürzere Animationen
- Offizielles Jellyfin-Logo (jellyfin-ux)
- Fernbedienungs-Tasten (Zurück, Play/Pause, Spulen, Menü)
- Server-Suche im LAN (UDP 7359) oder manuelle Adresse
- HTTP im Heimnetz und optional selbstsignierte HTTPS-Zertifikate

## Bauen

Voraussetzungen: JDK 17, Android SDK (API 35).

```bash
cd FireTV
echo "sdk.dir=/path/to/Android/Sdk" > local.properties

./gradlew :core:test
./gradlew :app:assembleDebug
```

Die Debug-APK liegt unter `FireTV/app/build/outputs/apk/debug/app-debug.apk`.

## Installation auf dem Fire TV Stick

```bash
adb connect <FIRE-TV-IP>
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n org.jellyfin.firetvweb/org.jellyfin.firetv.connect.ConnectActivity
```

Paketname: `org.jellyfin.firetvweb` — parallel zur offiziellen Android-TV-App installierbar.

## Wiedergabe und Downloads

Play oder eine Fassung startet **ExoPlayer** (Direct Stream mit API-Token, sonst HLS). Amazon-WebView spielt keine MKV-Dateien. Downloads erscheinen in der System-Downloadliste und im Ordner `Downloads/Jellyfin`.
