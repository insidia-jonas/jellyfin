/**
 * Fire TV Live TV overlay.
 *
 * Replaces movie-style IChannel / Live TV library cards with a real channel
 * list from /LiveTv/Channels?addCurrentProgram=true. Play goes straight to
 * ExoPlayer with the full channel queue for zap.
 *
 * Do not rewrite image URLs. Do not add extra card focus rings.
 */
(function () {
    if (window.__firetvLive) {
        return;
    }
    window.__firetvLive = true;

    var channels = [];
    var filter = "";
    var loading = false;
    var lastKey = "";
    var syncTimer = 0;

    function german() {
        var lang = String((document.documentElement && document.documentElement.lang) || navigator.language || "").toLowerCase();
        return lang.indexOf("de") === 0;
    }

    function hash() {
        return String(location.hash || "").toLowerCase();
    }

    function pageTitle() {
        var node = document.querySelector(".libraryPage .pageTitle, .headerTitle, h1, .sectionTitle");
        return node ? String(node.textContent || "").replace(/\s+/g, " ").trim() : "";
    }

    function isGuidePage() {
        return hash().indexOf("guide") !== -1;
    }

    function isLiveHash() {
        var h = hash();
        return h.indexOf("livetv") !== -1 || h.indexOf("live-tv") !== -1;
    }

    function looksLikeLiveLibrary() {
        if (isGuidePage()) {
            return false;
        }
        if (isLiveHash()) {
            return true;
        }
        if (/live[\s-]?tv/i.test(pageTitle())) {
            return true;
        }
        var cards = document.querySelectorAll(".card, .posterItem");
        if (cards.length < 3) {
            return false;
        }
        var hits = 0;
        for (var i = 0; i < cards.length; i++) {
            var type = String(cards[i].getAttribute("data-type") || "").toLowerCase();
            var text = String(cards[i].textContent || "");
            if (type === "tvchannel" || type === "program" || type === "livetvprogram" ||
                /Jetzt:|Danach:|\b\d+\s+Sender\b/.test(text)) {
                hits += 1;
            }
        }
        return hits >= 3 && hits >= cards.length * 0.4;
    }

    function auth() {
        var api = window.ApiClient;
        var device = {};
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
            appVersion: device.appVersion || "2.2.0"
        };
    }

    function clock(iso) {
        if (!iso) {
            return "";
        }
        var time = String(iso).split("T")[1] || "";
        return time.slice(0, 5);
    }

    function nowLine(item) {
        var program = item.CurrentProgram;
        if (!program || !program.Name) {
            return "";
        }
        var start = clock(program.StartDate);
        var end = clock(program.EndDate);
        var range = start && end ? " (" + start + "–" + end + ")" : (start ? " (" + start + ")" : "");
        return (german() ? "Jetzt: " : "Now: ") + program.Name + range;
    }

    function posterUrl(item) {
        var client = window.ApiClient;
        if (!client || !item || !item.Id || typeof client.getImageUrl !== "function") {
            return "";
        }
        if (item.ImageTags && item.ImageTags.Primary) {
            return client.getImageUrl(item.Id, {
                type: "Primary",
                tag: item.ImageTags.Primary,
                maxHeight: 160
            });
        }
        return "";
    }

    function fetchChannels() {
        var client = window.ApiClient;
        if (!client || typeof client.getJSON !== "function") {
            return Promise.resolve([]);
        }
        var url = client.getUrl("LiveTv/Channels", {
            userId: client.getCurrentUserId(),
            addCurrentProgram: true,
            enableImages: true,
            fields: "ChannelNumber,Overview,Tags",
            limit: 800
        });
        return client.getJSON(url).then(function (result) {
            return ((result && result.Items) || []).filter(function (item) {
                return item && item.Id;
            });
        }).catch(function () {
            return [];
        });
    }

    function ensure() {
        var box = document.getElementById("firetv-live");
        if (box) {
            return box;
        }
        box = document.createElement("div");
        box.id = "firetv-live";
        box.className = "verticalSection";
        var host = document.querySelector(".libraryPage, .liveTvPage, .padded-right.padded-bottom-page, .mainAnimatedPage") || document.body;
        if (host.firstChild) {
            host.insertBefore(box, host.firstChild);
        } else {
            host.appendChild(box);
        }
        return box;
    }

    function visibleChannels() {
        var q = filter.replace(/\s+/g, " ").trim().toLowerCase();
        if (!q) {
            return channels.slice();
        }
        return channels.filter(function (item) {
            var blob = [item.Name, item.Number, item.ChannelNumber, nowLine(item)]
                .join(" ")
                .toLowerCase();
            return blob.indexOf(q) !== -1;
        });
    }

    function play(item, list) {
        var creds = auth();
        var queue = (list && list.length ? list : channels).slice();
        var payload = {
            ids: queue.map(function (channel) { return channel.Id; }),
            items: queue.map(function (channel) {
                return {
                    Id: channel.Id,
                    Name: channel.Name,
                    OriginalTitle: channel.Name,
                    Type: "TvChannel",
                    MediaType: "Video",
                    IsLiveStream: true,
                    ChannelId: channel.Id,
                    ExternalId: channel.ExternalId,
                    Overview: nowLine(channel)
                };
            }),
            serverAddress: creds.serverAddress,
            accessToken: creds.accessToken,
            userId: creds.userId,
            deviceId: creds.deviceId,
            deviceName: creds.deviceName,
            appName: creds.appName,
            appVersion: creds.appVersion,
            startPositionTicks: 0
        };
        var current = payload.items.filter(function (entry) { return entry.Id === item.Id; })[0] || payload.items[0];
        if (current !== payload.items[0]) {
            payload.items = [current].concat(payload.items.filter(function (entry) { return entry.Id !== current.Id; }));
        }
        if (window.NativePlayer && window.NativePlayer.loadPlayer) {
            window.NativePlayer.loadPlayer(JSON.stringify(payload));
            return;
        }
        if (window.Emby && window.Emby.Page && typeof window.playbackManager !== "undefined") {
            window.playbackManager.play({ items: [item], fullscreen: true });
        }
    }

    function render() {
        var box = ensure();
        box.innerHTML = "";
        var head = document.createElement("div");
        head.className = "firetv-live-head";
        var title = document.createElement("div");
        title.className = "firetv-live-title";
        title.textContent = german() ? "Live TV" : "Live TV";
        var meta = document.createElement("div");
        meta.className = "firetv-live-count";
        var shown = visibleChannels();
        meta.textContent = shown.length + (german() ? " Sender" : " channels");
        var search = document.createElement("input");
        search.type = "search";
        search.className = "firetv-live-search";
        search.placeholder = german() ? "Sender oder Sendung" : "Channel or show";
        search.value = filter;
        search.setAttribute("aria-label", search.placeholder);
        search.addEventListener("input", function () {
            filter = search.value;
            render();
            var again = document.querySelector("#firetv-live .firetv-live-search");
            if (again) {
                again.focus();
                try {
                    again.setSelectionRange(filter.length, filter.length);
                } catch (e) { /* ignore */ }
            }
        });
        head.appendChild(title);
        head.appendChild(meta);
        head.appendChild(search);
        var list = document.createElement("div");
        list.className = "firetv-live-list";
        if (!shown.length) {
            var empty = document.createElement("div");
            empty.className = "firetv-live-empty";
            empty.textContent = loading
                ? (german() ? "Sender werden geladen…" : "Loading channels…")
                : (german() ? "Keine Sender. Tuner und Guide auf dem Server prüfen." : "No channels. Check the tuner and guide on the server.");
            list.appendChild(empty);
        }
        shown.forEach(function (item, index) {
            var row = document.createElement("button");
            row.type = "button";
            row.className = "firetv-live-row";
            row.setAttribute("data-id", item.Id);
            row.setAttribute("data-type", "TvChannel");
            row.tabIndex = 0;
            var logo = document.createElement("div");
            logo.className = "firetv-live-logo";
            var url = posterUrl(item);
            if (url) {
                logo.style.backgroundImage = "url('" + String(url).replace(/'/g, "%27") + "')";
            }
            var copy = document.createElement("div");
            copy.className = "firetv-live-copy";
            var name = document.createElement("div");
            name.className = "firetv-live-name";
            var number = item.Number || item.ChannelNumber || "";
            name.textContent = number ? number + "  " + (item.Name || "") : (item.Name || "");
            var now = document.createElement("div");
            now.className = "firetv-live-now";
            now.textContent = nowLine(item) || (german() ? "Keine EPG-Daten" : "No guide data");
            copy.appendChild(name);
            copy.appendChild(now);
            row.appendChild(logo);
            row.appendChild(copy);
            row.addEventListener("focus", function () { row.classList.add("focused"); });
            row.addEventListener("blur", function () { row.classList.remove("focused"); });
            row.addEventListener("click", function (event) {
                event.preventDefault();
                event.stopPropagation();
                play(item, shown);
            });
            list.appendChild(row);
            if (index === 0 && document.activeElement !== search) {
                window.setTimeout(function () { row.focus(); }, 40);
            }
        });
        box.appendChild(head);
        box.appendChild(list);
    }

    function show(on) {
        document.documentElement.classList.toggle("firetv-live-on", on);
        if (document.body) {
            document.body.classList.toggle("firetv-live-on", on);
        }
        var box = document.getElementById("firetv-live");
        if (!on && box) {
            box.setAttribute("data-firetv-hidden", "1");
        } else if (box) {
            box.removeAttribute("data-firetv-hidden");
        }
    }

    function load() {
        var key = hash() + "|" + pageTitle();
        if (loading) {
            return;
        }
        if (channels.length && lastKey === key) {
            show(true);
            render();
            return;
        }
        lastKey = key;
        loading = true;
        show(true);
        render();
        fetchChannels().then(function (items) {
            loading = false;
            channels = items;
            render();
        });
    }

    function hide() {
        show(false);
    }

    function sync() {
        if (looksLikeLiveLibrary()) {
            load();
        } else {
            hide();
        }
    }

    function interceptCardPlay(event) {
        if (!looksLikeLiveLibrary()) {
            return;
        }
        var card = event.target && event.target.closest
            ? event.target.closest(".card, .posterItem, .firetv-live-row")
            : null;
        if (!card || card.classList.contains("firetv-live-row")) {
            return;
        }
        var type = String(card.getAttribute("data-type") || "").toLowerCase();
        var text = String(card.textContent || "");
        var live = type === "tvchannel" || type === "program" || type === "livetvprogram" ||
            /Jetzt:|Danach:/.test(text);
        if (!live) {
            return;
        }
        var id = card.getAttribute("data-id");
        if (!id) {
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        var match = channels.filter(function (item) { return item.Id === id; })[0];
        if (match) {
            play(match, channels);
            return;
        }
        play({ Id: id, Name: text.split("  ·  ")[0] || text, Type: "TvChannel", IsLiveStream: true }, channels);
    }

    function start() {
        document.addEventListener("click", interceptCardPlay, true);
        window.addEventListener("hashchange", function () {
            lastKey = "";
            sync();
        });
        var observer = new MutationObserver(function () {
            window.clearTimeout(syncTimer);
            syncTimer = window.setTimeout(sync, 240);
        });
        if (document.documentElement) {
            observer.observe(document.documentElement, { childList: true, subtree: true });
        }
        sync();
    }

    window.FireTvLive = {
        sync: sync,
        play: play
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start);
    } else {
        start();
    }
})();
