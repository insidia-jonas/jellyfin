# Jellyfin for Fire TV (Web-Oberfläche)

Diese App zeigt **dieselbe vollständige Jellyfin-Web-Oberfläche** wie die [iOS-App](https://github.com/jellyfin/jellyfin-ios): Der Fire TV Stick lädt `jellyfin-web` vom Server in einer WebView und spricht mit dem Gerät über `NativeShell`.

Das ist bewusst **kein** API-only-Client wie [jellyfin-androidtv](https://github.com/jellyfin/jellyfin-androidtv). Bibliothek, Dashboard, Plugins, Themes und Live-TV kommen aus der Web-UI. **Film- und Serienwiedergabe** läuft über ExoPlayer, **Downloads** über den Android DownloadManager. Die Oberfläche verwendet eine **1920 CSS-Pixel breite TV-Ansicht**, die in das tatsächliche App-Fenster eingepasst wird; die Höhe folgt dem verfügbaren Platz.

## English

Fire TV client that hosts the full Jellyfin web UI (same architecture as the official iOS app: WebView + NativeShell). Video playback uses ExoPlayer so selecting a movie cannot freeze Amazon WebView. Downloads use Android DownloadManager. The 1920 CSS-pixel TV layout is scaled to the actual window, with automatic viewport height.

## Änderungen in 2.4.1

Die globale Suche sendet pro Suchbegriff eine Anfrage mit unverändertem Titel und Jahr. Alte Anfragen werden beim Weitertippen oder Seitenwechsel abgebrochen; verspätete Antworten dürfen die neue Ansicht nicht ersetzen. Lokale Sender- und Dashboardfilter lösen keine globale Suche aus. Die Treasure-Maps-Web-Erweiterung nutzt auf Fire TV die gemeinsame Trefferansicht, statt eine zweite Suche zu starten.

## Änderungen in 2.4.0

- Korrigierte Skalierung für den Fire TV Cube: `WebView.setInitialScale` erwartet einen Prozentwert ohne erneute Division durch die Android-Bildschirmdichte. Konkurrierende Viewport-Metatags werden entfernt. Siehe [Android-WebView-Dokumentation](https://developer.android.com/reference/android/webkit/WebView#setInitialScale(int)).
- Virtualisierte Senderliste mit seitenweisem API-Abruf ohne 800-Sender-Grenze. Nur der sichtbare Bereich und ein kleiner Vorlauf werden gerendert. Die Sendersuche umfasst alle geladenen Sender; Zeilen und Fokus bleiben bei EPG-Aktualisierungen erhalten.
- Favoriten, zuletzt gesehene Sender, Gruppenfilter anhand der Server-Tags sowie gespeicherte Filter und Listenposition. Der Zustand ist nach Server und Benutzer getrennt und lokal auf dem Gerät gespeichert. Nach der Wiedergabe erhält der zuletzt abgespielte Sender den Fokus.
- „Jetzt/Danach“ und Fortschritt aktualisieren sich automatisch; UTC-Zeiten werden in der Gerätezeitzone angezeigt. Im nativen Player wird der Guide des laufenden Senders ebenfalls aktualisiert. Während der Wiedergabe pausiert das Hintergrund-Polling der Senderliste.
- Logos behalten ihre Proportionen. Ein Cache mit höchstens drei gleichzeitigen Abrufen, 32 Bildern / 3 MiB im Arbeitsspeicher und 96 Bildern / 8 MiB in IndexedDB übersteht Neustarts. Bei Fehlern bleibt ein vorhandenes Logo oder ein lesbares Textkürzel erhalten.
- Live-TV verwendet den bestehenden ExoPlayer beim Zappen weiter. Neue Senderwahlen verwerfen veraltete Antworten, brechen laufende HTTP-Anfragen ab und geben vorherige Tuner frei. Ein kurzer Vorlauf von 180 ms bündelt schnelle Tastenfolgen.
- Konsistente Puffergrenzen beheben den auf dem Cube protokollierten Player-Absturz. Bei Live-TV-Ladefehlern gibt es höchstens drei Wiederholungen mit 1/2/4 Sekunden Abstand; ein Stillstand beim Puffern wird nach 15 Sekunden behandelt. Erfolgreiche Wiedergabe setzt den Wiederholungszähler nach 30 Sekunden zurück.
- Die vom Server-Fork eingebundene `web/livetv-overview.js` wird innerhalb dieser App durch die gebündelte TV-Liste ersetzt. Dadurch laufen keine zwei Senderansichten gleichzeitig. Fernsehprogramm, Aufnahmen und Zeitpläne bleiben über die vorhandenen Web-Tabs erreichbar.
- Weniger GPU- und Speicheraufwand durch entfernte Filter, Schatten, erzwungene Hardware-Layer und Offscreen-Prerasterisierung. Hardwarebeschleunigung bleibt aktiv.
- Leere, noch ladende Startseitenbereiche bleiben sichtbar. Verspätete Senderantworten können keine fremde Seite überschreiben.
- Java-Zeit-APIs werden für Android API 25 über [Core Library Desugaring](https://developer.android.com/studio/write/java8-support#library-desugaring) bereitgestellt.

Validierung: 80 Kotlin-Tests, 22 Web-Regressionstests, Debug-Build und Android Lint (keine Fehler). Die Web-Tests prüfen unter anderem 1.200 Sender, Navigation über virtuelle Zeilengrenzen, EPG-Wechsel, Abbruch verspäteter Antworten, Benutzerwechsel und Logo-Cache-Neustarts.

Gerätetest am 01.10.2026: Fire TV Cube AFTR, Android 9 / API 28, Amazon WebView 138, 528 Sender. Die Liste hielt 14 Zeilen gleichzeitig im DOM; bei 120 gemessenen Leerlauf-Frames gab es keine Änderungen an den Kindelementen. 60 synthetische D-Pad-Schritte im echten WebView: Median 24,5 ms, 95. Perzentil 32,5 ms bis zum nächsten Animationsframe. Die sichtbaren 14 Logos wurden geladen. Bei zwei erfolgreichen nativen Umschaltungen meldete der Player das erste Bild nach 735 und 912 ms; der Player wurde wiederverwendet. Bei drei schnell aufeinanderfolgenden ADB-Tastenereignissen wurde nur die letzte Antwort übernommen. Ein zuvor gestarteter Sender blieb über zwölf Sekunden im Pufferzustand, bevor weitergeschaltet wurde. Diese kurze Stichprobe bewertet weder alle Sender noch die langfristige Stream-Stabilität oder die Tonqualität.

Mit der abschließenden APK wurden außerdem die Tabs Fernsehprogramm, Aufnahmen und Kanäle geprüft. Ein absichtlich nicht vorhandener Sender löste genau drei Wiederholungen mit 1/2/4 Sekunden Abstand und anschließend die Rückkehr zur Senderliste aus.

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
./gradlew :app:lintDebug
```

Die Debug-APK liegt unter `FireTV/app/build/outputs/apk/debug/app-debug.apk`.

Für eine separate Testinstallation mit dem Namen **Jellyfin IPTV Test**:

```bash
./gradlew :core:test :app:assembleDebug :app:lintDebug -PfiretvPreview=true
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n org.jellyfin.firetvweb.preview/org.jellyfin.firetv.connect.ConnectActivity
```

Diese Variante hat den Paketnamen `org.jellyfin.firetvweb.preview` und eigene App-Daten. Sie lässt eine bereits installierte, anders signierte Haupt-App bestehen. Ohne `-PfiretvPreview=true` entsteht die normale Variante. Beide Varianten verwenden denselben APK-Ausgabepfad; die gewünschte Datei vor dem nächsten Varianten-Build kopieren.

Wiedergabe-Diagnose (Anfragedauer, erstes Bild, Pufferpausen, Wiederholungen und verworfene Frames, ohne Stream-URLs oder Tokens):

```bash
adb logcat -s FireTvPlayback
```

Die Web-Regressionstests benötigen Node.js ab Version 18:

```bash
cd FireTV/tests/web
npm ci
npm test
```

Lokale Debug-Builds verwenden den lokalen Android-Debug-Schlüssel. Ein Update der unter `sideload/` abgelegten APK benötigt denselben Signierschlüssel wie diese APK; ein anderer Schlüssel führt zu `INSTALL_FAILED_UPDATE_INCOMPATIBLE`. Eine Deinstallation entfernt App-Daten wie die gespeicherte Anmeldung. Die mitgelieferte APK wird durch den lokalen Build nicht ersetzt.

## Installation auf dem Fire TV Stick

```bash
adb connect <FIRE-TV-IP>
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n org.jellyfin.firetvweb/org.jellyfin.firetv.connect.ConnectActivity
```

Paketname: `org.jellyfin.firetvweb` — parallel zur offiziellen Android-TV-App installierbar.

## Wiedergabe und Downloads

Play oder eine Fassung startet **ExoPlayer** (Direct Stream mit API-Token, sonst HLS). Amazon-WebView spielt keine MKV-Dateien. Downloads erscheinen in der System-Downloadliste und im Ordner `Downloads/Jellyfin`.
