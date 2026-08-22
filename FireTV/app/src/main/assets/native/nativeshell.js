/**
 * NativeShell for the Fire TV web-shell client.
 *
 * Forces the 1920x1080 TV layout, registers a native ExoPlayer so selecting a
 * release/version does not freeze the Amazon WebView HTML5 player, and keeps
 * jellyfin-web as the full UI (same architecture as the iOS app).
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

    forceTvViewport();
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", forceTvViewport);
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
            appVersion: "1.1.0"
        };
    }

    var device = readDeviceInfo();

    var features = [
        "exit",
        "displaylanguage",
        "displaymode",
        "fullscreenchange",
        "physicalvolumecontrol",
        "remotecontrol",
        "subtitleappearancesettings",
        "subtitleburnsettings",
        "screensaver",
        "multiserver",
        "clientsettings",
        "externallinks"
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
            { Format: "ass", Method: "Encode" },
            { Format: "ssa", Method: "Encode" },
            { Format: "pgssub", Method: "Encode" }
        ],
        ResponseProfiles: []
    };

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
        window.ExoPlayer = this;
    }

    FireTvExoPlayerPlugin.prototype.play = function (options) {
        options = options || {};
        var items = options.items || [];
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
        return !playOptions || playOptions.fullscreen !== false;
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
        return Promise.resolve(exoPlayerProfile);
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
        return false;
    };
    FireTvExoPlayerPlugin.prototype.setAudioStreamIndex = function () { };
    FireTvExoPlayerPlugin.prototype.setSubtitleStreamIndex = function () { };
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

    window.ExoPlayerPlugin = function () {
        return Promise.resolve(FireTvExoPlayerPlugin);
    };

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
        downloadFile: function () { },
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
            return ["ExoPlayerPlugin"];
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
        getDeviceProfile: function () {
            return exoPlayerProfile;
        },
        getSyncProfile: function () {
            return exoPlayerProfile;
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
