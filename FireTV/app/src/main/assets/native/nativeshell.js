/**
 * NativeShell for the Fire TV web-shell client.
 *
 * Mirrors the iOS/Android mobile apps: jellyfin-web is hosted in a WebView and talks to
 * the device through window.NativeShell. Layout is forced to "tv" so the full living-room
 * web UI (not the API-only Android TV client) is shown.
 */
(function () {
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
            appVersion: "1.0.0"
        };
    }

    var device = readDeviceInfo();

    var features = [
        "exit",
        "htmlaudioautoplay",
        "htmlvideoautoplay",
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
        openUrl: function (url, target) {
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
            // Empty: keep jellyfin-web's own HTML5 player so the full web UI is used.
            return [];
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
                return profileBuilder({
                    enableMkvProgressive: false,
                    disableHlsVideoAudioCodecs: ["truehd", "dca", "dtshd", "opus"]
                });
            }
            return null;
        },
        getSyncProfile: function (profileBuilder) {
            return this.getDeviceProfile(profileBuilder);
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
