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
        var meta = document.querySelector('meta[name="viewport"]');
        if (!meta) {
            meta = document.createElement("meta");
            meta.setAttribute("name", "viewport");
            head.insertBefore(meta, head.firstChild);
        }
        meta.setAttribute(
            "content",
            "width=1920, height=1080, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover"
        );
        document.documentElement.style.width = "100%";
        document.documentElement.style.height = "100%";
        if (document.body) {
            document.body.style.width = "100%";
            document.body.style.height = "100%";
            document.body.style.overflow = "hidden";
        }
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
            "html,body{width:100%!important;height:100%!important;overflow:hidden!important;background:#0B0E14!important;}",
            ".layout-tv .backdrop-container,.layout-tv .backgroundContainer,.layout-tv .backdropContainer{",
            "  filter:none!important;transform:none!important;background:#0B0E14!important;",
            "}",
            ".layout-tv .backdropContainer .backdropImage,.layout-tv .backgroundContainer .backdropImage{",
            "  opacity:.16!important;filter:none!important;",
            "}",
            "video,audio[controls]{display:none!important;width:0!important;height:0!important;}"
        ].join("");
        parent.appendChild(style);
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

    window.FireTvCanExit = function () {
        try {
            var blocking = document.querySelectorAll(".dialog, .actionSheet, .dialogContainer");
            for (var i = 0; i < blocking.length; i++) {
                if (blocking[i].offsetParent !== null) {
                    return false;
                }
            }
            if (document.querySelector(".mainDrawer-open, .drawer-open")) {
                return false;
            }
            var hash = String(location.hash || "").toLowerCase();
            if (hash.indexOf("details") !== -1 || hash.indexOf("item") !== -1 || hash.indexOf("wizard") !== -1) {
                return false;
            }
            if (!hash || hash === "#" || hash === "#/" || hash.indexOf("home") !== -1) {
                return true;
            }
            if (document.querySelector(".homeSectionsContainer, .homePage")) {
                return true;
            }
        } catch (e) { /* ignore */ }
        return false;
    };

    window.FireTvGuard = function () {
        forceTvViewport();
        injectPerformanceCss();
        patchHtml5Media();
    };

    forceTvViewport();
    injectPerformanceCss();
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
            appVersion: "1.3.2"
        };
    }

    var device = readDeviceInfo();

    var features = [
        "exit",
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
        "htmlaudioautoplay"
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
                VideoCodec: "h264,hevc,vp8,vp9,av1,mpeg2video,mpeg4",
                AudioCodec: "aac,mp3,ac3,eac3,flac,opus,pcm,dts"
            },
            {
                Container: "mp3,aac,flac,wav,ogg,opus,m4a",
                Type: "Audio"
            }
        ],
        TranscodingProfiles: [
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
                    { Condition: "LessThanEqual", Property: "VideoLevel", Value: "51", IsRequired: false }
                ]
            }
        ],
        SubtitleProfiles: [
            { Format: "vtt", Method: "External" },
            { Format: "srt", Method: "External" },
            { Format: "ttml", Method: "External" },
            { Format: "subrip", Method: "External" },
            { Format: "ass", Method: "External" },
            { Format: "ssa", Method: "Encode" },
            { Format: "pgssub", Method: "Encode" }
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
        return String(mediaType || "").toLowerCase() === "video";
    };
    FireTvExoPlayerPlugin.prototype.canQueueMediaType = function (mediaType) {
        return this.canPlayMediaType(mediaType);
    };
    FireTvExoPlayerPlugin.prototype.canPlayItem = function (item, playOptions) {
        if (!window.NativePlayer || !window.NativePlayer.isEnabled()) {
            return false;
        }
        if (playOptions && playOptions.fullscreen === false) {
            return false;
        }
        if (this.playbackManager && this.playbackManager.syncPlayEnabled) {
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
        return null;
    };
    FireTvExoPlayerPlugin.prototype.volume = function () {
        return null;
    };
    FireTvExoPlayerPlugin.prototype.setVolume = function (vol) {
        if (window.NativePlayer) {
            window.NativePlayer.setVolume(parseInt(vol, 10) || 0);
        }
    };
    FireTvExoPlayerPlugin.prototype.getVolume = function () {
        return 100;
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
    FireTvExoPlayerPlugin.prototype.nextTrack = function () { };
    FireTvExoPlayerPlugin.prototype.previousTrack = function () { };
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

    window.FireTvRemote = {
        send: function (key) {
            var init = {
                key: key,
                code: key,
                bubbles: true,
                cancelable: true,
                composed: true
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
