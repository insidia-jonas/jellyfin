/**
 * Native ExoPlayer plugin for jellyfin-web.
 *
 * jellyfin-web loads this as an ES module from `/native/ExoPlayerPlugin.js`
 * (same contract as jellyfin-android). Play always goes to the Fire TV
 * ExoPlayer activity — never to HTML5 <video>, which freezes Amazon WebView
 * on MKV/H.265.
 */
function collectAuth() {
    const api = window.ApiClient;
    let device = {};
    try {
        if (window.NativeInterface && window.NativeInterface.getDeviceInformation) {
            device = JSON.parse(window.NativeInterface.getDeviceInformation()) || {};
        }
    } catch (e) {
        device = {};
    }
    return {
        serverAddress: api && api.serverAddress ? api.serverAddress() : "",
        accessToken: api && api.accessToken ? api.accessToken() : "",
        userId: api && api.getCurrentUserId ? api.getCurrentUserId() : "",
        deviceId: device.deviceId || "",
        deviceName: device.deviceName || "Fire TV",
        appName: device.appName || "Jellyfin Fire TV",
        appVersion: device.appVersion || "1.3.0"
    };
}

function toPayload(options) {
    options = options || {};
    const items = options.items || [];
    const ids = options.ids && options.ids.length
        ? options.ids
        : items.map(function (item) { return item.Id; }).filter(Boolean);
    const auth = collectAuth();
    return Object.assign({}, options, auth, {
        ids: ids,
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
        })
    });
}

export class ExoPlayerPlugin {
    constructor({ events, playbackManager, loading }) {
        window.ExoPlayer = this;
        window.FireTvExoPlayerPluginClass = ExoPlayerPlugin;

        this.events = events;
        this.playbackManager = playbackManager;
        this.loading = loading;

        this.name = "ExoPlayer";
        this.type = "mediaplayer";
        this.id = "exoplayer";
        this.priority = -1;
        this.isLocalPlayer = true;

        this._currentTime = 0;
        this._paused = true;
        this._nativePlayer = window.NativePlayer;
    }

    async play(options) {
        this._paused = false;
        if (this._nativePlayer) {
            this._nativePlayer.loadPlayer(JSON.stringify(toPayload(options)));
        }
        if (this.loading && this.loading.hide) {
            this.loading.hide();
        }
    }

    shuffle() {}

    instantMix() {}

    queue() {}

    queueNext() {}

    canPlayMediaType(mediaType) {
        return String(mediaType || "").toLowerCase() === "video";
    }

    canQueueMediaType(mediaType) {
        return this.canPlayMediaType(mediaType);
    }

    canPlayItem(item, playOptions) {
        if (!this._nativePlayer || !this._nativePlayer.isEnabled()) {
            return false;
        }
        if (playOptions && playOptions.fullscreen === false) {
            return false;
        }
        if (this.playbackManager && this.playbackManager.syncPlayEnabled) {
            return false;
        }
        return true;
    }

    async stop(destroyPlayer) {
        if (this._nativePlayer) {
            this._nativePlayer.stopPlayer();
        }
        if (destroyPlayer) {
            this.destroy();
        }
    }

    nextTrack() {}

    previousTrack() {}

    seek(ticks) {
        if (this._nativePlayer) {
            this._nativePlayer.seekTicks(ticks);
        }
    }

    currentTime(ms) {
        if (ms !== undefined && this._nativePlayer) {
            this._nativePlayer.seekMs(ms);
        }
        return this._currentTime;
    }

    duration() {
        return null;
    }

    volume(volume) {
        if (volume !== undefined) {
            this.setVolume(volume);
        }
        return null;
    }

    getVolume() {
        return 100;
    }

    setVolume(vol) {
        if (this._nativePlayer) {
            this._nativePlayer.setVolume(parseInt(vol, 10) || 0);
        }
    }

    volumeUp() {}

    volumeDown() {}

    isMuted() {
        return false;
    }

    setMute() {}

    toggleMute() {}

    paused() {
        return this._paused;
    }

    pause() {
        this._paused = true;
        if (this._nativePlayer) {
            this._nativePlayer.pausePlayer();
        }
    }

    unpause() {
        this._paused = false;
        if (this._nativePlayer) {
            this._nativePlayer.resumePlayer();
        }
    }

    playPause() {
        if (this._paused) {
            this.unpause();
        } else {
            this.pause();
        }
    }

    canSetAudioStreamIndex() {
        return true;
    }

    setAudioStreamIndex(index) {
        if (this._nativePlayer && this._nativePlayer.setAudioStreamIndex) {
            this._nativePlayer.setAudioStreamIndex(index);
        }
    }

    setSubtitleStreamIndex(index) {
        if (this._nativePlayer && this._nativePlayer.setSubtitleStreamIndex) {
            this._nativePlayer.setSubtitleStreamIndex(index);
        }
    }

    async changeAudioStream() {}

    async changeSubtitleStream() {}

    getPlaylist() {
        return Promise.resolve([]);
    }

    getCurrentPlaylistItemId() {}

    setCurrentPlaylistItem() {
        return Promise.resolve();
    }

    removeFromPlaylist() {
        return Promise.resolve();
    }

    destroy() {
        if (this._nativePlayer) {
            this._nativePlayer.destroyPlayer();
        }
    }

    async getDeviceProfile() {
        if (window.NativeShell && window.NativeShell.AppHost && window.NativeShell.AppHost.getDeviceProfile) {
            return window.NativeShell.AppHost.getDeviceProfile();
        }
        return {
            Name: "Jellyfin Fire TV ExoPlayer",
            MaxStreamingBitrate: 120000000,
            DirectPlayProfiles: [{ Type: "Video" }, { Type: "Audio" }],
            CodecProfiles: [],
            SubtitleProfiles: [],
            TranscodingProfiles: []
        };
    }
}
