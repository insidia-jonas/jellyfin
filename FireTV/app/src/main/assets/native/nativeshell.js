/**
 * NativeShell for the Fire TV web-shell client.
 *
 * Must exist before jellyfin-web's plugin loader runs. jellyfin-web then:
 *   1. reads NativeShell.getPlugins() → ["ExoPlayerPlugin"]
 *   2. awaits window.ExoPlayerPlugin() which dynamically imports
 *      /native/ExoPlayerPlugin.js (ES module, same as jellyfin-android)
 *
 * Also forces the 1920×1080 TV layout, blocks HTML5 <video> (Amazon WebView
 * freezes on MKV), and forwards downloads to Android DownloadManager.
 *
 * Do not patch HTMLImageElement.src or ApiClient image URLs: Amazon WebView
 * re-enters the setter (freeze on Search) and dropping fillWidth/fillHeight
 * makes library posters such as Treasure Maps fail to load.
 *
 * Cinema UX lives in /native/tv-cinema.css and /native/tvExperience.js.
 */
(function () {
    function forceTvViewport() {
        try {
            localStorage.setItem("layout", "tv");
        } catch (e) { /* private mode */ }
        var head = document.head || document.getElementsByTagName("head")[0];
        if (!head) {
            return;
        }
        var viewports = document.querySelectorAll('meta[name="viewport"]');
        var meta = viewports[0];
        // The hosted document can add its viewport after our document-start hook.
        // Keep one authority for layout rather than two competing scale limits.
        for (var i = 1; i < viewports.length; i++) { viewports[i].remove(); }
        if (!meta) {
            meta = document.createElement("meta");
            meta.setAttribute("name", "viewport");
            head.insertBefore(meta, head.firstChild);
        }
        meta.setAttribute(
            "content",
            "width=1920, user-scalable=no, viewport-fit=cover"
        );
        document.documentElement.style.width = "100%";
        document.documentElement.style.height = "100%";
        if (document.body) {
            document.body.style.width = "100%";
            document.body.style.height = "100%";
            document.body.style.overflow = "hidden";
        }
        // Scaling is owned natively (WebViewDisplayFit.setInitialScale). A JS zoom
        // here raced with it and double-shrank the UI on the Cube — do not re-add.
    }

    function injectPerformanceCss() {
        if (document.getElementById("firetv-perf-css")) {
            return;
        }
        var parent = document.head || document.documentElement;
        if (!parent) {
            return;
        }
        var style = document.createElement("style");
        style.id = "firetv-perf-css";
        style.textContent = [
            "html,body{width:100%!important;height:100%!important;overflow:hidden!important;background:#07090F!important;}",
            ".layout-tv,.layout-tv body{cursor:none!important;}",
            ".layout-tv ::-webkit-scrollbar{width:0!important;height:0!important;}",
            ".layout-tv .backdrop-container,.layout-tv .backgroundContainer,.layout-tv .backdropContainer{",
            "  filter:none!important;transform:none!important;background:#07090F!important;",
            "}",
            ".layout-tv .backdropContainer .backdropImage,.layout-tv .backgroundContainer .backdropImage{",
            "  opacity:.18!important;filter:none!important;",
            "}",
            ".layout-tv .skinHeader{",
            "  background-color:rgba(7,9,15,.92)!important;",
            "  border-bottom:1px solid rgba(255,255,255,.06)!important;",
            "}",
            ".layout-tv .sectionTitle,.layout-tv .sectionTitleTextButton{",
            "  letter-spacing:.02em!important;",
            "}",
            ".layout-tv .dialog{",
            "  background:#151A24!important;",
            "  border-radius:16px!important;",
            "  box-shadow:none!important;",
            "}",
            "video{display:none!important;width:0!important;height:0!important;}"
        ].join("");
        parent.appendChild(style);
    }

    function loadCinemaLayer() {
        var parent = document.head || document.documentElement;
        if (!parent) {
            return;
        }
        if (!document.querySelector('script[src="/native/tvNavigation.js"]')) {
            var navigation = document.createElement('script');
            navigation.src = '/native/tvNavigation.js';
            parent.appendChild(navigation);
        }
        if (!document.getElementById("firetv-cinema-css")) {
            var link = document.createElement("link");
            link.id = "firetv-cinema-css";
            link.rel = "stylesheet";
            link.href = "/native/tv-cinema.css";
            parent.appendChild(link);
        }
        if (!document.querySelector('script[src="/native/tvExperience.js"]')) {
            var script = document.createElement("script");
            script.src = "/native/tvExperience.js";
            script.async = true;
            parent.appendChild(script);
        }
        if (document.body && !document.querySelector('script[src="/native/channelHealth.js"]')) {
            var health = document.createElement("script");
            health.src = "/native/channelHealth.js";
            health.async = true;
            parent.appendChild(health);
        }
        function loadLive() {
            if (document.querySelector('script[src="/native/tvLive.js"]')) { return; }
            var live = document.createElement("script");
            live.src = "/native/tvLive.js";
            parent.appendChild(live);
        }
        if (window.FireTvLogos) { loadLive(); }
        else if (!document.querySelector('script[src="/native/tvLogos.js"]')) {
            var logos = document.createElement("script");
            logos.src = "/native/tvLogos.js";
            logos.onload = logos.onerror = loadLive;
            parent.appendChild(logos);
        }
    }

    function patchHtml5Media() {
        if (window.__firetvMediaPatched) {
            return;
        }
        window.__firetvMediaPatched = true;
        function block(proto) {
            if (!proto || proto.__firetvPatched) {
                return;
            }
            proto.__firetvPatched = true;
            proto.play = function () {
                try {
                    this.pause();
                    this.removeAttribute("src");
                    while (this.firstChild) {
                        this.removeChild(this.firstChild);
                    }
                } catch (e) { /* ignore */ }
                return Promise.resolve();
            };
        }
        block(window.HTMLVideoElement && window.HTMLVideoElement.prototype);
        document.addEventListener("play", function (event) {
            var el = event.target;
            if (!el || el.tagName !== "VIDEO") {
                return;
            }
            event.preventDefault();
            event.stopPropagation();
            try {
                el.pause();
            } catch (e) { /* ignore */ }
        }, true);
    }

    // Track SPA routes explicitly: hidden Home DOM survives inside the web router.
    var backRoutes = [String(location.hash || "#/home.html")];
    var navigatingBack = false;
    var backFallback = 0;
    function rememberRoute(replace) {
        var route = String(location.hash || "#/home.html");
        if (backRoutes[backRoutes.length - 1] === route) { return; }
        var previous = backRoutes.lastIndexOf(route);
        if (navigatingBack && previous >= 0) { backRoutes = backRoutes.slice(0, previous + 1); }
        else if (replace) { backRoutes[backRoutes.length - 1] = route; }
        else { backRoutes.push(route); if (backRoutes.length > 80) { backRoutes.shift(); } }
        navigatingBack = false;
        clearTimeout(backFallback);
    }
    ["pushState", "replaceState"].forEach(function (method) {
        var original = history[method];
        history[method] = function () {
            var result = original.apply(this, arguments);
            rememberRoute(method === "replaceState");
            return result;
        };
    });
    window.addEventListener("hashchange", function () { rememberRoute(false); });
    window.addEventListener("popstate", function () { rememberRoute(false); });
    function hasBackOverlay() {
        return Array.prototype.some.call(document.querySelectorAll(".dialog, .actionSheet, .dialogContainer, .mainDrawer-open, .drawer-open"), function (el) {
            return el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
        });
    }
    window.FireTvCanExit = function () {
        return !hasBackOverlay() && /^#?\/?(?:home(?:\.html)?)?(?:\?.*)?$/.test(String(location.hash || ""));
    };
    window.FireTvNavigation = {
        back: function () {
            if (window.FireTvLive && window.FireTvLive.closeGuide && window.FireTvLive.closeGuide()) { return "handled"; }
            if (hasBackOverlay()) { window.FireTvRemote.send("Escape"); return "handled"; }
            if (window.FireTvCanExit()) { return "root"; }
            if (navigatingBack) { return "handled"; }
            var current = String(location.hash || "");
            var target = backRoutes.length > 1 ? backRoutes[backRoutes.length - 2] : "#/home.html";
            if (backRoutes.length > 1) {
                navigatingBack = true;
                history.back();
                backFallback = setTimeout(function () {
                    if (String(location.hash || "") === current) { location.hash = target; }
                    navigatingBack = false;
                }, 500);
            } else { location.hash = target; }
            return "handled";
        }
    };

    window.FireTvGuard = function () {
        forceTvViewport();
        injectPerformanceCss();
        loadCinemaLayer();
        patchHtml5Media();
    };

    forceTvViewport();
    injectPerformanceCss();
    loadCinemaLayer();
    patchHtml5Media();
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", window.FireTvGuard);
    } else {
        window.FireTvGuard();
    }

    if (window.NativeShell && window.NativeShell.AppHost) {
        return;
    }

    function readDeviceInfo() {
        try {
            if (window.NativeInterface && window.NativeInterface.getDeviceInformation) {
                return JSON.parse(window.NativeInterface.getDeviceInformation());
            }
        } catch (e) {
            console.warn("NativeInterface.getDeviceInformation failed", e);
        }
        return {
            deviceId: "firetv-web",
            deviceName: "Fire TV",
            appName: "Jellyfin Fire TV",
            appVersion: "2.5.0"
        };
    }

    var device = readDeviceInfo();

    var features = [
        "exit",
        "exitmenu",
        "displaylanguage",
        "displaymode",
        "physicalvolumecontrol",
        "remotecontrol",
        "subtitleappearancesettings",
        "subtitleburnsettings",
        "screensaver",
        "multiserver",
        "clientsettings",
        "externallinks",
        "filedownload",
        "downloadmanagement",
        "htmlaudioautoplay",
        "livetv"
    ];

    var exoPlayerProfile = {
        Name: "Jellyfin Fire TV ExoPlayer",
        MaxStreamingBitrate: 120000000,
        MaxStaticBitrate: 100000000,
        MusicStreamingTranscodingBitrate: 320000,
        DirectPlayProfiles: [
            {
                Container: "mp4,m4v,mov,mkv,webm,ts,mpegts,avi",
                Type: "Video",
                VideoCodec: "h264,hevc,vp8,vp9,av1",
                AudioCodec: "aac,mp3,ac3,eac3,flac,opus,pcm,dts"
            },
            {
                Container: "mp3,aac,flac,wav,ogg,opus,m4a",
                Type: "Audio"
            }
        ],
        TranscodingProfiles: [
            {
                Container: "mp4",
                Type: "Video",
                VideoCodec: "h264,hevc,av1",
                AudioCodec: "aac,ac3,eac3",
                Protocol: "http",
                Context: "Streaming",
                MaxAudioChannels: "8",
                CopyTimestamps: true
            },
            {
                Container: "ts",
                Type: "Video",
                VideoCodec: "h264",
                AudioCodec: "aac,ac3",
                Protocol: "hls",
                Context: "Streaming",
                MaxAudioChannels: "6",
                MinSegments: "1",
                BreakOnNonKeyFrames: true
            },
            {
                Container: "mp3",
                Type: "Audio",
                AudioCodec: "mp3",
                Protocol: "http",
                Context: "Streaming"
            }
        ],
        ContainerProfiles: [],
        CodecProfiles: [
            {
                Type: "Video",
                Codec: "h264",
                Conditions: [
                    { Condition: "EqualsAny", Property: "VideoProfile", Value: "high|main|baseline|constrained baseline", IsRequired: false },
                    { Condition: "LessThanEqual", Property: "VideoLevel", Value: "51", IsRequired: false },
                    { Condition: "EqualsAny", Property: "VideoRotation", Value: "0|90|180|270", IsRequired: false }
                ]
            },
            {
                Type: "Video",
                Conditions: [
                    { Condition: "EqualsAny", Property: "VideoRotation", Value: "0|90|180|270", IsRequired: false }
                ]
            }
        ],
        SubtitleProfiles: [
            { Format: "vtt", Method: "External" },
            { Format: "srt", Method: "External" },
            { Format: "ttml", Method: "External" },
            { Format: "subrip", Method: "External" },
            { Format: "ass", Method: "External" },
            { Format: "vobsub", Method: "External" },
            { Format: "dvdsub", Method: "External" },
            { Format: "ssa", Method: "Encode" },
            { Format: "pgssub", Method: "Encode" },
            { Format: "vobsub", Method: "Encode" },
            { Format: "dvdsub", Method: "Encode" }
        ],
        ResponseProfiles: []
    };

    function copyProfile() {
        return JSON.parse(JSON.stringify(exoPlayerProfile));
    }

    function FireTvExoPlayerPlugin(deps) {
        deps = deps || {};
        this.events = deps.events;
        this.playbackManager = deps.playbackManager;
        this.loading = deps.loading;
        this.name = "ExoPlayer";
        this.type = "mediaplayer";
        this.id = "exoplayer";
        this.priority = -1;
        this.isLocalPlayer = true;
        this._currentTime = 0;
        this._duration = null;
        this._volume = 100;
        this._paused = true;
        this._nativePlayer = window.NativePlayer;
        window.ExoPlayer = this;
    }

    FireTvExoPlayerPlugin.prototype.play = function (options) {
        options = options || {};
        var items = options.items || [];
        var ids = options.ids && options.ids.length
            ? options.ids
            : items.map(function (item) { return item.Id; }).filter(Boolean);
        var payload = {
            items: items.map(function (item) {
                return {
                    Id: item.Id,
                    Name: item.Name || item.Path || "",
                    ServerId: item.ServerId,
                    Type: item.Type,
                    MediaType: item.MediaType,
                    IsLiveStream: !!(item.IsLiveStream || item.Type === "TvChannel" || item.Type === "Program" || item.Type === "LiveTvProgram"),
                    ChannelId: item.ChannelId,
                    ExternalId: item.ExternalId,
                    OriginalTitle: item.OriginalTitle,
                    Overview: item.Overview,
                    RunTimeTicks: item.RunTimeTicks,
                    ProductionYear: item.ProductionYear
                };
            }),
            ids: ids,
            mediaSourceId: options.mediaSourceId || options.MediaSourceId,
            audioStreamIndex: options.audioStreamIndex,
            subtitleStreamIndex: options.subtitleStreamIndex,
            startPositionTicks: options.startPositionTicks || 0,
            serverAddress: window.ApiClient ? window.ApiClient.serverAddress() : "",
            accessToken: window.ApiClient ? window.ApiClient.accessToken() : "",
            userId: window.ApiClient ? window.ApiClient.getCurrentUserId() : "",
            deviceId: device.deviceId,
            deviceName: device.deviceName,
            appName: device.appName,
            appVersion: device.appVersion
        };
        this._paused = false;
        if (window.NativePlayer) {
            window.NativePlayer.loadPlayer(JSON.stringify(payload));
        }
        if (this.loading && this.loading.hide) {
            this.loading.hide();
        }
    };
    FireTvExoPlayerPlugin.prototype.canPlayMediaType = function (mediaType) {
        var kind = String(mediaType || "").toLowerCase();
        return kind !== "book" && kind !== "photo";
    };
    FireTvExoPlayerPlugin.prototype.canQueueMediaType = function (mediaType) {
        return this.canPlayMediaType(mediaType);
    };
    FireTvExoPlayerPlugin.prototype.canPlayItem = function (item, playOptions) {
        if (!window.NativePlayer || !window.NativePlayer.isEnabled()) {
            return false;
        }
        if (this.playbackManager && this.playbackManager.syncPlayEnabled) {
            return false;
        }
        var live = !!(item && (item.IsLiveStream || item.Type === "TvChannel" || item.Type === "Program" || item.Type === "LiveTvProgram"));
        if (!live && playOptions && playOptions.fullscreen === false) {
            return false;
        }
        return true;
    };
    FireTvExoPlayerPlugin.prototype.stop = function (destroyPlayer) {
        if (window.NativePlayer) {
            window.NativePlayer.stopPlayer();
        }
        if (destroyPlayer) {
            this.destroy();
        }
        return Promise.resolve();
    };
    FireTvExoPlayerPlugin.prototype.pause = function () {
        this._paused = true;
        if (window.NativePlayer) {
            window.NativePlayer.pausePlayer();
        }
    };
    FireTvExoPlayerPlugin.prototype.unpause = function () {
        this._paused = false;
        if (window.NativePlayer) {
            window.NativePlayer.resumePlayer();
        }
    };
    FireTvExoPlayerPlugin.prototype.playPause = function () {
        if (this._paused) {
            this.unpause();
        } else {
            this.pause();
        }
    };
    FireTvExoPlayerPlugin.prototype.paused = function () {
        return this._paused;
    };
    FireTvExoPlayerPlugin.prototype.seek = function (ticks) {
        if (window.NativePlayer) {
            window.NativePlayer.seekTicks(ticks);
        }
    };
    FireTvExoPlayerPlugin.prototype.currentTime = function (ms) {
        if (ms !== undefined && window.NativePlayer) {
            window.NativePlayer.seekMs(ms);
        }
        return this._currentTime;
    };
    FireTvExoPlayerPlugin.prototype.duration = function () {
        return this._duration;
    };
    FireTvExoPlayerPlugin.prototype.volume = function (value) {
        if (value !== undefined) {
            this.setVolume(value);
        }
        return this._volume;
    };
    FireTvExoPlayerPlugin.prototype.setVolume = function (vol) {
        if (window.NativePlayer) {
            window.NativePlayer.setVolume(parseInt(vol, 10) || 0);
        }
    };
    FireTvExoPlayerPlugin.prototype.getVolume = function () {
        return this._volume;
    };
    FireTvExoPlayerPlugin.prototype.isMuted = function () {
        return false;
    };
    FireTvExoPlayerPlugin.prototype.setMute = function () { };
    FireTvExoPlayerPlugin.prototype.destroy = function () {
        if (window.NativePlayer) {
            window.NativePlayer.destroyPlayer();
        }
    };
    FireTvExoPlayerPlugin.prototype.getDeviceProfile = function () {
        return Promise.resolve(copyProfile());
    };
    FireTvExoPlayerPlugin.prototype.getPlaylist = function () {
        return Promise.resolve([]);
    };
    FireTvExoPlayerPlugin.prototype.shuffle = function () { };
    FireTvExoPlayerPlugin.prototype.instantMix = function () { };
    FireTvExoPlayerPlugin.prototype.queue = function () { };
    FireTvExoPlayerPlugin.prototype.queueNext = function () { };
    FireTvExoPlayerPlugin.prototype.nextTrack = function () {
        if (window.NativePlayer && window.NativePlayer.nextTrack) {
            window.NativePlayer.nextTrack();
        }
    };
    FireTvExoPlayerPlugin.prototype.previousTrack = function () {
        if (window.NativePlayer && window.NativePlayer.previousTrack) {
            window.NativePlayer.previousTrack();
        }
    };
    FireTvExoPlayerPlugin.prototype.canSetAudioStreamIndex = function () {
        return true;
    };
    FireTvExoPlayerPlugin.prototype.setAudioStreamIndex = function (index) {
        if (window.NativePlayer && window.NativePlayer.setAudioStreamIndex) {
            window.NativePlayer.setAudioStreamIndex(index);
        }
    };
    FireTvExoPlayerPlugin.prototype.setSubtitleStreamIndex = function (index) {
        if (window.NativePlayer && window.NativePlayer.setSubtitleStreamIndex) {
            window.NativePlayer.setSubtitleStreamIndex(index);
        }
    };
    FireTvExoPlayerPlugin.prototype.changeAudioStream = function () {
        return Promise.resolve();
    };
    FireTvExoPlayerPlugin.prototype.changeSubtitleStream = function () {
        return Promise.resolve();
    };
    FireTvExoPlayerPlugin.prototype.setCurrentPlaylistItem = function () {
        return Promise.resolve();
    };
    FireTvExoPlayerPlugin.prototype.removeFromPlaylist = function () {
        return Promise.resolve();
    };

    window.FireTvExoPlayerPluginClass = FireTvExoPlayerPlugin;

    var plugins = ["ExoPlayerPlugin"];
    window.ExoPlayerPlugin = async function () {
        try {
            var pluginDefinition = await import("/native/ExoPlayerPlugin.js");
            if (pluginDefinition && pluginDefinition.ExoPlayerPlugin) {
                return pluginDefinition.ExoPlayerPlugin;
            }
        } catch (e) {
            console.warn("FireTV: ExoPlayerPlugin module failed, using bundled class", e);
        }
        return FireTvExoPlayerPlugin;
    };

    function postDownload(info) {
        if (!window.NativeInterface) {
            return;
        }
        var token = "";
        try {
            token = window.ApiClient && window.ApiClient.accessToken ? window.ApiClient.accessToken() : "";
        } catch (e) {
            token = "";
        }
        var payload;
        if (typeof info === "string") {
            payload = info;
            try {
                var parsed = JSON.parse(info);
                if (parsed && typeof parsed === "object" && !parsed.accessToken && token) {
                    parsed.accessToken = token;
                    payload = JSON.stringify(parsed);
                }
            } catch (e) { /* keep original string */ }
        } else if (Array.isArray(info)) {
            payload = JSON.stringify({ files: info, accessToken: token });
        } else {
            var body = info || {};
            if (!body.accessToken && token) {
                body.accessToken = token;
            }
            payload = JSON.stringify(body);
        }
        if (window.NativeInterface.downloadFiles) {
            window.NativeInterface.downloadFiles(payload);
        }
    }

    window.NativeShell = {
        enableFullscreen: function () {
            if (window.NativeInterface) {
                window.NativeInterface.enableFullscreen();
            }
        },
        disableFullscreen: function () {
            if (window.NativeInterface) {
                window.NativeInterface.disableFullscreen();
            }
        },
        openUrl: function (url) {
            if (window.NativeInterface) {
                window.NativeInterface.openUrl(String(url || ""));
            }
        },
        downloadFile: function (downloadInfo) {
            postDownload(downloadInfo);
        },
        downloadFiles: function (downloadInfo) {
            postDownload(downloadInfo);
        },
        openDownloadManager: function () {
            if (window.NativeInterface && window.NativeInterface.openDownloadManager) {
                window.NativeInterface.openDownloadManager();
            }
        },
        updateMediaSession: function (mediaInfo) {
            if (window.NativeInterface) {
                window.NativeInterface.updateMediaSession(JSON.stringify(mediaInfo || {}));
            }
        },
        hideMediaSession: function () {
            if (window.NativeInterface) {
                window.NativeInterface.hideMediaSession();
            }
        },
        updateVolumeLevel: function (value) {
            if (window.NativeInterface) {
                window.NativeInterface.updateVolumeLevel(Number(value) || 0);
            }
        },
        openClientSettings: function () {
            if (window.NativeInterface) {
                window.NativeInterface.openClientSettings();
            }
        },
        selectServer: function () {
            if (window.NativeInterface) {
                window.NativeInterface.openServerSelection();
            }
        },
        findServers: function (timeoutMs) {
            return new Promise(function (resolve) {
                var settled = false;
                window.__firetvFindServersDone = function (servers) {
                    if (settled) {
                        return;
                    }
                    settled = true;
                    resolve(Array.isArray(servers) ? servers : []);
                };
                try {
                    if (window.NativeInterface && window.NativeInterface.findServersAsync) {
                        window.NativeInterface.findServersAsync(timeoutMs || 3000);
                    } else {
                        resolve([]);
                        return;
                    }
                } catch (e) {
                    resolve([]);
                    return;
                }
                window.setTimeout(function () {
                    if (!settled) {
                        settled = true;
                        resolve([]);
                    }
                }, (timeoutMs || 3000) + 1500);
            });
        },
        onLocalUserSignedIn: function (user, token) {
            if (window.NativeInterface && window.NativeInterface.onLocalUserSignedIn) {
                window.NativeInterface.onLocalUserSignedIn(String(user || ""), String(token || ""));
            }
        },
        onLocalUserSignedOut: function () {
            if (window.NativeInterface && window.NativeInterface.onLocalUserSignedOut) {
                window.NativeInterface.onLocalUserSignedOut();
            }
        },
        getPlugins: function () {
            return plugins;
        }
    };

    window.NativeShell.AppHost = {
        init: function () {
            return Promise.resolve(device);
        },
        appName: function () {
            return device.appName;
        },
        appVersion: function () {
            return device.appVersion;
        },
        deviceId: function () {
            return device.deviceId;
        },
        deviceName: function () {
            return device.deviceName;
        },
        exit: function () {
            if (window.NativeInterface) {
                window.NativeInterface.exitApp();
            }
        },
        getDefaultLayout: function () {
            return "tv";
        },
        getDeviceProfile: function (profileBuilder) {
            if (typeof profileBuilder === "function") {
                try {
                    profileBuilder({ enableMkvProgressive: false });
                } catch (e) { /* builder is optional */ }
            }
            return copyProfile();
        },
        getSyncProfile: function () {
            return copyProfile();
        },
        screen: function () {
            return { width: 1920, height: 1080 };
        },
        supports: function (command) {
            if (!command) {
                return false;
            }
            return features.indexOf(String(command).toLowerCase()) !== -1;
        }
    };

    window.FireTvPlayerSync = {
        apply: function (state) {
            if (window.FireTvLive && window.FireTvLive.onPlaybackState) {
                window.FireTvLive.onPlaybackState(state);
            }
            var player = window.ExoPlayer;
            if (!player || !state) {
                return;
            }
            if (typeof state.positionMs === "number") {
                player._currentTime = state.positionMs;
            }
            if (typeof state.durationMs === "number") {
                player._duration = state.durationMs > 0 ? state.durationMs : null;
            }
            if (typeof state.paused === "boolean") {
                player._paused = state.paused;
            }
            if (typeof state.volume === "number") {
                player._volume = state.volume;
            }
            var ev = player.events;
            if (ev && typeof ev.trigger === "function" && state.event) {
                try {
                    ev.trigger(player, state.event);
                } catch (e) { /* keep playback going */ }
            }
        }
    };

    window.FireTvRemote = {
        send: function (key) {
            var init = {
                key: key,
                code: key,
                bubbles: true,
                cancelable: true,
                composed: true, keyCode: key === "Escape" ? 27 : 0, which: key === "Escape" ? 27 : 0
            };
            try {
                document.dispatchEvent(new KeyboardEvent("keydown", init));
            } catch (e) {
                var fallback = document.createEvent("Event");
                fallback.initEvent("keydown", true, true);
                fallback.key = key;
                document.dispatchEvent(fallback);
            }
        }
    };
})();
