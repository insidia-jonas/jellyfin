# Jellyfin for Fire TV (Web-Oberfläche)

Diese App zeigt **dieselbe vollständige Jellyfin-Web-Oberfläche** wie die [iOS-App](https://github.com/jellyfin/jellyfin-ios): Der Fire TV Stick lädt `jellyfin-web` vom Server in einer WebView und spricht mit dem Gerät über `NativeShell`.

Das ist bewusst **kein** API-only-Client wie [jellyfin-androidtv](https://github.com/jellyfin/jellyfin-androidtv). Bibliothek, Dashboard, Plugins, Themes und Live-TV kommen aus der Web-UI. **Film- und Serienwiedergabe** läuft über ExoPlayer, **Downloads** über den Android DownloadManager. Die Oberfläche verwendet eine **1920 CSS-Pixel breite TV-Ansicht**, die in das tatsächliche App-Fenster eingepasst wird; die Höhe folgt dem verfügbaren Platz.

## English

Fire TV client that hosts the full Jellyfin web UI (same architecture as the official iOS app: WebView + NativeShell). Video playback uses ExoPlayer so selecting a movie cannot freeze Amazon WebView. Downloads use Android DownloadManager. The 1920 CSS-pixel TV layout is scaled to the actual window, with automatic viewport height.

## Serverkorrektur für Treasure-Maps-Navigation (02.10.2026)

Startseiten-Empfehlungen und Suchtreffer werden nicht mehr als Kinder des Kategorie-Hauptordners gespeichert. Dadurch löst das Öffnen von Treasure Maps keine rekursive Löschung dieser Titelkarten aus. Gleichzeitige Listenaufrufe führen die Ordnerabstimmung nacheinander aus; alte Titelkarten behalten ihre IDs, Details und Benutzerdaten. Dasselbe gilt für Titel, die aus einer wechselnden Kategorie-Liste herausfallen: Deren Bereinigung öffnet keine entfernten Detailordner mehr. Die Ordnerabstimmung berücksichtigt alle gespeicherten Kinder, unabhängig von der angefragten Seitengröße. Die installierte APK 2.4.6 nutzt die Serverkorrektur ohne weiteres App-Update.

Validierung: 236 Live-TV-/Channel-Tests auf dem Raspberry erfolgreich, einschließlich fünf neuer Navigationstests. Vier abschließende Öffnungen auf dem Cube dauerten 277 / 270 / 253 / 265 ms; beim ersten Test nach Serverneustart 2.081 ms. Im Browser wurden 1.941 / 490 / 152 / 192 ms gemessen. Jeweils zehn Kategorien, keine Skriptfehler. Drei zusätzliche Zyklen mit je 24 Empfehlungen ließen die Kategorien unverändert und die Titel-Details erreichbar; die Kategorie-API antwortete dabei in 48–50 ms. Zuvor wurden auf dem Cube zwei parallele Kategorieanfragen mit etwa 39 Sekunden und gleichzeitiger mehrfacher Löschung derselben Titelkarten protokolliert. Diese Messungen betreffen den Kategorie-Einstieg. Das erstmalige Befüllen der Filmkategorie benötigte in einer weiteren Stichprobe noch etwa 26 Sekunden; externe Katalogabfragen und die Materialisierung neuer Titel sind damit nicht generell beschleunigt.

## Änderungen in 2.4.6

- Sendernamen verwenden eine eigene CSS-Klasse statt der großen Seitenüberschrift. Name, Status und Jetzt/Danach passen in die neu bemessenen Senderzeilen; lange Texte werden mit Auslassungspunkten begrenzt.
- Abgebrochene Stream-Aufrufe geben ihren Tuner-Verbraucher nach dem Probe-Abbruch frei. Andere Zuschauer desselben Streams bleiben verbunden.
- Die App wartet bei Live-TV-Aufrufen bis zu 35 Sekunden auf die Serverantwort, damit ein konfigurierter Quellenwechsel abgeschlossen werden kann. Das gesamte Wiederherstellungsbudget bleibt auf 45 Sekunden begrenzt.

- Netzwerkfehler aus FFmpeg (Timeout, Verbindungsablehnung oder DNS-Fehler) werden als Anbieterproblem erkannt und zählen nicht als ungültige Medien oder wiederholt ausgefallener Sender.

Validierung am 02.10.2026: 231 Live-TV-Tests mit echtem FFmpeg auf dem Raspberry, 142 API-Tests, 34 Streamverwaltungs-Tests, 85 Kotlin-Tests und 59 Web-Tests erfolgreich. Server-Build, Formatprüfung und Android Lint ohne Fehler. Auf dem Cube passen alle gemessenen Textblöcke (114 px) in die Senderzeilen (135 px). Nach Abbruch von Sky Cinema Action wurde der Tuner sofort freigegeben; Warner TV Film zeigte anschließend nach 1.189 ms ein Bild. Sky Cinema Premieren kehrte nach 45 Sekunden zur Liste zurück, ohne offene Verbraucher zu hinterlassen.

Die genannten Sky-Quellen lieferten auch im direkten FFmpeg-Test auf dem Raspberry keine Videobilder: Action und Premieren HD scheiterten über alle acht konfigurierten Hosts mit Netzwerk-Timeouts. Warner TV Film lieferte über drei davon dekodierbares Video. Diese externe Verfügbarkeit wird durch das Client-Update nicht behoben.

## Änderungen in 2.4.5

- Der Server zeigt in Web und Fire TV pro Sender „Zuletzt erreichbar“, „Instabil“, „Mehrfach ausgefallen“ oder „Ungeprüft“ mit Prüfzeit an. Die Anzeige verwendet nur gespeicherte Beobachtungen; das Öffnen einer Senderliste stellt keine Anbieter-Verbindung her. Ergebnisse verfallen nach 30 Minuten.
- Laufende MPEG-TS-Streams liefern Startzeit, Datenmenge und Unterbrechungen über den bestehenden gemeinsamen Proxy. Tatsächlicher Wiedergabefortschritt bestätigt zusätzlich HLS-/Radio-Wiedergaben. Normales Stoppen und Senderwechsel zählen nicht als Fehler.
- Fehlgeschlagene Fire-TV-Wiedergaben werden über Jellyfins bestehende Sitzungs-API zurückgemeldet. „Wiedergabe fehlgeschlagen“ bleibt gelb sichtbar, auch wenn Daten am Server ankommen; ein Geräte-/Decoderfehler allein markiert einen Sender nicht als tot. Browser und APK verwenden dasselbe Statusmodul, das beim Android-Build als Asset übernommen wird.
- Der Server prüft jede Minute höchstens einen fälligen Favoriten oder innerhalb der letzten sieben Tage verwendeten Sender. Eine Prüfung dekodiert eine Sekunde Video bzw. Radio-Audio mit dem vorhandenen FFmpeg, mit zwölf Sekunden Gesamtlimit. Pro Sender liegen mindestens 15 Minuten zwischen Prüfungen. Bei Wiedergaben, Aufnahmen, bevorstehenden Aufnahmen und während zwei Minuten nach einer Tuner-Nutzung wird nicht geprüft. Eine beginnende Wiedergabe/Aufnahme bricht die Prüfung ab und wartet auf das Ende ihres Prozesses, bevor sie den Anbieter kontaktiert. Auch bei unbekanntem `TunerCount=0` gibt es höchstens eine Prüfung und keine parallele Jellyfin-Tuner-Nutzung.
- Kontofehler und Anbieter-Limits erzeugen einen gesonderten Hinweis und zehn Minuten Pause für automatische Prüfungen. Ein Sender wird erst nach zwei mindestens 30 Sekunden auseinanderliegenden Fehlerbeobachtungen als ausgefallen markiert. HTTP 200 ohne dekodierbare Medien reicht nicht für eine erfolgreiche Vorprüfung.
- Der MPEG-TS-Proxy versucht nach zwei bestätigten Ausfällen eine konfigurierte Ersatzquelle nur für den betroffenen Stream. Die Wiederherstellung endet nach höchstens vier Fehlversuchen; kurze Datenhäppchen setzen dieses Budget nicht zurück. Fire TV begrenzt eine Wiederherstellungsphase zusätzlich auf 45 Sekunden und lässt dem Server Zeit zum Quellenwechsel. Die Ersatzquellen stammen aus der bestehenden M3U-Tuner-Konfiguration; es werden keine neuen Anbieter angelegt.
- Fire-TV-Favoriten werden über Jellyfins Favoriten-API gespeichert. Bisher nur lokal gespeicherte Favoriten werden einmalig übertragen, damit der Server sie vorprüfen kann.
- Live-TV erlaubt H.264-Startbilder aus Nicht-IDR-I-Slices. „Das Erste“ lieferte im Cube-Test dekodierbares Video ohne IDR-Bilder und blieb mit der Standardeinstellung dauerhaft beim Puffern. Die Einstellung betrifft den MPEG-TS-Extractor bei Live-Wiedergaben; kurzzeitige Bildfehler beim Einstieg sind bei solchen Quellen möglich ([Media3-Hinweis](https://developer.android.com/media/media3/exoplayer/troubleshooting#why-do-some-mpeg-ts-files-fail-to-play)).

Administration: `EnableChannelHealthProbes` in der Live-TV-Konfiguration schaltet aktive Vorprüfungen ab; passive Beobachtung bleibt verfügbar. Der Verlauf liegt unter `<DataPath>/livetv/channel-health.json`, ohne Anbieter-URLs und Zugangsdaten. `GET /LiveTv/ChannelHealth?ids=<bis zu 100 Jellyfin-Item-IDs>` benötigt Live-TV-Berechtigung und liefert nur zugängliche Sender. Hintergrundprüfungen berücksichtigen Jellyfin-Nutzung; gleichzeitige Streams in externen Anbieter-Apps sind dem Server nicht bekannt. Die Statusanzeige ist eine zeitlich begrenzte Beobachtung, keine Verfügbarkeitsgarantie.

Validierung am 02.10.2026: 222 Live-TV-Tests auf dem Raspberry einschließlich echter FFmpeg-Dekodierung und Prozessabbruch, 141 API-Tests, 85 Kotlin-Tests und 59 Web-Tests erfolgreich; Server-Build und Android Lint ohne Fehler. Auf dem Cube wurden erfolgreiche Wiedergaben (ZDF und Das Erste), die Rückmeldung eines gescheiterten Starts und Statusanzeigen geprüft. Die automatische Leerlaufprüfung dekodierte RTL HD in 4,1 Sekunden. Quellenwechsel und Anbieterfehler wurden mit kontrollierten HTTP-Testquellen geprüft; eine Langzeitmessung aller Anbieterkanäle ist damit nicht verbunden.

## Änderungen in 2.4.4

Die App heißt in beiden Paketvarianten **Jellyfin Fire TV**. Das vorhandene quadratische Jellyfin-Symbol wird für App-Icon und Logo verwendet; das breite Bild mit Schriftzug ausschließlich als TV-Banner. Dadurch wird im Launcher kein 16:9-Schriftzug in ein quadratisches Symbol gequetscht. Anmeldung und App-Daten bleiben beim Update der bereits installierten Variante erhalten. Die Versionsanzeige kommt aus der APK; der Verbindungsbildschirm enthält keine veraltete, fest eingetragene Versionsnummer mehr.

## Änderungen in 2.4.3

- Live TV übernimmt die Gruppenordner und deren Sender vom Server. Gruppen öffnen eine eigene Liste; Ordner werden nicht in die Wiedergabe-Warteschlange aufgenommen. Sendernamen und Jetzt/Danach bleiben getrennt.
- Die native JSON-Auswertung dekodiert Unicode-Escapes korrekt. Insbesondere `\u0026` in Jellyfins Wiedergabe-URLs darf nicht als wörtliche Zeichenfolge an den Player gelangen; das führte zu fehlgeschlagenen Stream-Aufrufen und drei Wiederholungen.
- Der Server prüft beim Öffnen von M3U-Sendern die tatsächlichen Codecs und verwendet den vorhandenen Probe-Cache. Erst nach der Umstellung auf Jellyfins gemeinsamen Stream darf Direct Play angeboten werden. Kompatible Sender benötigen dadurch keine Software-Transkodierung auf dem Raspberry; unbekannte oder inkompatible Formate behalten den Ausweichweg.
- Treasure Maps räumt beim Seitenwechsel Download-Ansichten, ausgeblendete Elemente und Statusabfragen auf. Verfügbare Versionen sind nach Sprache aufklappbar; Deutsch steht zuerst, mehrsprachige Versionen erscheinen nur einmal.
- Mit Plugin 1.0.3 lassen sich Filme über Radarr und Serien über Sonarr anfordern. Konfigurierte Qualitätsprofile, Medienordner und Indexer bestimmen die automatische Auswahl. Vorhandene überwachte Titel werden nicht erneut angelegt oder gesucht. Die Auswahl einer konkreten Version bleibt ein direkter Treasure-Maps-Download.

Validierung: 84 Kotlin-Tests, 50 Web-Regressionstests, 201 Live-TV-Server-Tests, 198 Plugin-Tests unter Linux sowie APK-Build und Android Lint. Die Tests prüfen unter anderem Gruppenwechsel, JSON-URLs, Navigation nach Downloads, Sprachgruppen und idempotente Anforderungen.

Gerätetest am 02.10.2026 auf dem Cube: neun Gruppen, darunter 68 Sender in „DE Germany“. Nach der Serverkorrektur kam das erste Bild bei bereits geprüften Sendern nach 1.230 ms („Das Erste“) und 714 ms („3Sat“); der Player wurde beim Wechsel wiederverwendet. Ein weiterer Sender ohne Probe-Cache benötigte 6.017 ms. In dieser kurzen Stichprobe gab es nach dem ersten Bild keine weiteren Pufferpausen oder Wiederholungen. Das ersetzt keinen Langzeittest aller Anbieterstreams.

## Änderungen in 2.4.2

Beim Wechsel von Live TV zu Filmen, Serien oder Treasure Maps wird die Senderliste sofort entfernt. Die Erweiterung berücksichtigt auch die Navigation über die History API des Webclients und Zurück/Vorwärts. Zwischengespeicherte Überschriften oder Senderkarten einer alten Seite können keine fremde Seite mehr als Live TV einstufen; Listen werden anhand ihres aktuellen API-Elternelements erkannt. Verspätete Antworten bleiben an die ursprüngliche Seite gebunden.

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

Für ein Update der früher als **Jellyfin IPTV Test** installierten Variante (nun ebenfalls **Jellyfin Fire TV**):

```bash
./gradlew :core:test :app:assembleDebug :app:lintDebug -PfiretvPreview=true
adb install -r app/build/outputs/apk/debug/app-debug.apk
adb shell am start -n org.jellyfin.firetvweb.preview/org.jellyfin.firetv.connect.ConnectActivity
```

Diese Variante behält den Paketnamen `org.jellyfin.firetvweb.preview`, damit bestehende Installationen samt Anmeldung aktualisiert werden können. Der Schalter bestimmt nur die Paketidentität, nicht den sichtbaren App-Namen. Eine bereits installierte, anders signierte Haupt-App bleibt bestehen. Ohne `-PfiretvPreview=true` entsteht die normale Variante. Beide Varianten verwenden denselben APK-Ausgabepfad; die gewünschte Datei vor dem nächsten Varianten-Build kopieren.

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
