/**
 * Fire TV Live TV overlay.
 *
 * Replaces movie-style IChannel / Live TV library cards with a real channel
 * list from /LiveTv/Channels?addCurrentProgram=true. Play goes straight to
 * ExoPlayer with the full channel queue for zap.
 *
 * Only our visible channel rows are mounted. Stock image URL helpers stay untouched.
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
    var requestToken = 0;
    var routeKey = "";
    var parentKey = "";
    var parentLive = false;
    var parentItem = null;
    var parentPending = false;
    var parentToken = 0;

    function german() {
        var lang = String((document.documentElement && document.documentElement.lang) || navigator.language || "").toLowerCase();
        return lang.indexOf("de") === 0;
    }

    function hash() {
        return String(location.hash || "").toLowerCase();
    }

    function isGuidePage() {
        return hash().indexOf("guide") !== -1;
    }

    function isLiveHash() {
        return /^#\/?(?:livetv|live-tv|channels)(?:[/?]|$)/.test(hash());
    }

    function injectedOwns() {
        return window.JellyfinLiveTvOverview && typeof window.JellyfinLiveTvOverview.ownsPage === "function" &&
            window.JellyfinLiveTvOverview.ownsPage();
    }

    function looksLikeLiveLibrary() {
        if (isGuidePage()) {
            return false;
        }
        if (injectedOwns() || document.getElementById("jf-livetv-overview")) {
            return false;
        }
        if (isLiveHash()) {
            // jellyfin-web switches these tabs without changing the route.
            var tab = document.querySelector(".skinHeader .emby-tab-button-active[data-index]");
            return !tab || ["0", "2"].indexOf(tab.getAttribute("data-index")) !== -1;
        }
        return parentKey === scopeKey() + "|" + hash() && parentLive;
    }

    function liveParent(item) {
        if (!item) { return false; }
        var ids = item.ProviderIds || {};
        if (ids.TreasureMaps || ids.treasuremaps || ids.TreasureMapsCategory || ids.treasuremapscategory
            || /treasure[\s-]?maps/i.test(item.Name || item.ChannelName || "")) { return false; }
        var folder = item.IsFolder || /^(?:Channel|ChannelFolderItem|Folder)$/.test(item.Type || "");
        return !!(folder && (ids.LiveTv || ids.livetv || /^g:/.test(item.ExternalId || item.SourceId || "")
            || /^live[\s-]?tv$/i.test(item.Name || "") || /\d+\s+Sender/i.test(item.Overview || "")));
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
            appVersion: device.appVersion || "2.4.4"
        };
    }

    function clock(iso) {
        if (!iso) {
            return "";
        }
        var date = new Date(iso);
        if (isNaN(date.getTime())) { return ""; }
        return ("0" + date.getHours()).slice(-2) + ":" + ("0" + date.getMinutes()).slice(-2);
    }

    function nowLine(item) {
        var program = item.CurrentProgram;
        if (!program || !program.Name) {
            var overview = String(item.OriginalTitle || item.Overview || "");
            var hit = overview.split(/\r?\n| {2}· {2}/).filter(function (line) {
                return /Jetzt:|Now:/i.test(line);
            })[0];
            return hit ? hit.trim() : "";
        }
        var start = clock(program.StartDate);
        var end = clock(program.EndDate);
        var range = start && end ? " (" + start + "–" + end + ")" : (start ? " (" + start + ")" : "");
        return (german() ? "Jetzt: " : "Now: ") + program.Name + range;
    }

    function nextLine(item) {
        var next = item.NextProgram;
        if (next && next.Name) {
            var start = clock(next.StartDate);
            var end = clock(next.EndDate);
            var range = start && end ? " (" + start + "–" + end + ")" : (start ? " (" + start + ")" : "");
            return (german() ? "Danach: " : "Next: ") + next.Name + range;
        }
        var overview = String(item.OriginalTitle || item.Overview || "");
        var hit = overview.split(/\r?\n| {2}· {2}/).filter(function (line) {
            return /Danach:|Next:/i.test(line);
        })[0];
        return hit ? hit.trim() : "";
    }

    function progressOf(item, nowMs) {
        var program = item.CurrentProgram || item;
        var start = Date.parse(program.StartDate || item.StartDate || item.PremiereDate || "");
        var end = Date.parse(program.EndDate || item.EndDate || "");
        if (start && end && end > start) {
            var ratio = ((nowMs - start) / (end - start)) * 100;
            if (ratio < 0) {
                return 0;
            }
            if (ratio > 100) {
                return 100;
            }
            return Math.round(ratio * 10) / 10;
        }
        if (program.CompletionPercentage != null) {
            return Math.max(0, Math.min(100, Number(program.CompletionPercentage)));
        }
        if (item.CompletionPercentage != null) {
            return Math.max(0, Math.min(100, Number(item.CompletionPercentage)));
        }
        return null;
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
                maxHeight: 160, maxWidth: 240, quality: 85
            });
        }
        return "";
    }

    function resolvePlaybackManager() {
        if (window.playbackManager && typeof window.playbackManager.play === "function") {
            return window.playbackManager;
        }
        if (window.JellyfinLiveTvOverview && typeof window.JellyfinLiveTvOverview.resolvePlaybackManager === "function") {
            return window.JellyfinLiveTvOverview.resolvePlaybackManager();
        }
        try {
            var req;
            var chunks = window.webpackChunk || (typeof self !== "undefined" && self.webpackChunk);
            if (chunks && typeof chunks.push === "function") {
                chunks.push([["jf-livetv-pm"], {}, function (r) { req = r; }]);
            }
            if (typeof req === "function") {
                var exp = req("./components/playback/playbackmanager.js");
                if (exp && exp.playbackManager && typeof exp.playbackManager.play === "function") {
                    window.playbackManager = exp.playbackManager;
                    return exp.playbackManager;
                }
            }
        } catch (e) { /* web host without webpack */ }
        return null;
    }

    function playableItem(item) {
        var copy = {};
        var key;
        for (key in item) {
            if (Object.prototype.hasOwnProperty.call(item, key)) {
                copy[key] = item[key];
            }
        }
        copy.Type = "TvChannel";
        copy.MediaType = "Video";
        copy.IsLiveStream = true;
        if (!copy.ChannelId) {
            copy.ChannelId = item.Id;
        }
        return copy;
    }

    function isGroup(item) {
        return !!(item && (item.IsFolder || item.Type === "ChannelFolderItem" || item.Type === "Folder"));
    }

    function play(item, list) {
        if (!item) { return; }
        if (isGroup(item)) { location.hash = "#/list?parentId=" + item.Id + "&ltvgroup=1"; return; }
        var creds = auth();
        var queue = (list && list.length ? list : channels).filter(function (entry) { return !isGroup(entry); });
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
            var index = payload.items.indexOf(current);
            payload.items = payload.items.slice(index).concat(payload.items.slice(0, index));
        }
        if (window.NativePlayer && window.NativePlayer.loadPlayer) {
            window.NativePlayer.loadPlayer(JSON.stringify(payload));
            return true;
        }
        var manager = resolvePlaybackManager();
        if (manager) {
            manager.play({ items: [playableItem(item)], fullscreen: true });
            return true;
        }
        // Web host without the native bridge: hand back to the injected Live TV
        // list, which owns a REST-only <video> player. Never leave a click dead.
        var overview = window.JellyfinLiveTvOverview;
        if (overview && typeof overview.openBuiltinPlayer === "function") {
            overview.openBuiltinPlayer(item, queue);
            return true;
        }
        return false;
    }

    function scopeKey() {
        var api = window.ApiClient;
        return (api && api.serverAddress ? api.serverAddress() : location.origin) + "|" +
            (api && api.getCurrentUserId ? api.getCurrentUserId() : "anonymous");
    }

    var stateKey = "";
    var saved = {};
    var shown = [];
    var byId = {};
    var rows = {};
    var viewport;
    var content;
    var rowHeight = 150;
    var frame = 0;
    var timer = 0;
    var guideTimer = 0;
    var guideLoading = false;
    var nextChannelsRefresh = 0;
    var statusText = "";
    var paused = false;
    var restoreAfterPlayback = false;
    var programs = {};
    var OVERSCAN = 4;

    function readState() {
        var key = scopeKey();
        if (key === stateKey) { return; }
        stateKey = key;
        try { saved = JSON.parse(localStorage.getItem("firetv-live-v2:" + key) || "{}"); } catch (e) { saved = {}; }
        saved.favorites = saved.favorites || {};
        saved.recent = saved.recent || [];
        saved.mode = saved.mode || "all";
        saved.group = saved.group || "";
        filter = saved.filter || "";
        channels = [];
        programs = {};
    }

    function persist() {
        saved.filter = filter;
        try { localStorage.setItem("firetv-live-v2:" + stateKey, JSON.stringify(saved)); } catch (e) { /* storage full */ }
    }

    function rememberPlayed(id) {
        if (!byId[id]) { return; }
        saved.recent = [id].concat(saved.recent.filter(function (other) { return other !== id; })).slice(0, 30);
        saved.lastId = id;
        persist();
    }

    function fetchChannels(token) {
        var client = window.ApiClient;
        if (!client || typeof client.getJSON !== "function") { return Promise.reject(new Error("No server")); }
        var out = [];
        var seen = {};
        var parent = parentLive && parentItem && parentItem.Id;
        function page(offset) {
            if (token !== requestToken) { return Promise.resolve([]); }
            var request = parent ? client.getItems(client.getCurrentUserId(), {
                ParentId: parent, Fields: "ChannelNumber,Overview,Tags,ProviderIds,ExternalId,OriginalTitle,StartDate,EndDate",
                SortBy: "SortName", SortOrder: "Ascending", StartIndex: offset, Limit: 200
            }) : client.getJSON(client.getUrl("LiveTv/Channels", {
                userId: client.getCurrentUserId(), addCurrentProgram: true, enableImages: true,
                fields: "ChannelNumber,Overview,Tags", startIndex: offset, limit: 200
            }));
            return request.then(function (result) {
                var batch = (result && result.Items) || [];
                var added = 0;
                batch.forEach(function (item) {
                    if (item && item.Id && !seen[item.Id]) { seen[item.Id] = true; out.push(item); added++; }
                });
                var total = result && result.TotalRecordCount;
                if (added && batch.length >= 200 && (!total || out.length < total)) {
                    return page(offset + batch.length);
                }
                return out;
            });
        }
        return page(0);
    }

    function visibleChannels() {
        var q = filter.replace(/\s+/g, " ").trim().toLowerCase();
        if (channels.length && channels.every(isGroup)) { return channels.filter(function (item) { return !q || item.Name.toLowerCase().indexOf(q) !== -1; }); }
        var source = saved.mode === "recent" ? saved.recent.map(function (id) { return byId[id]; }).filter(Boolean) : channels;
        return source.filter(function (item) {
            if (saved.mode === "favorites" && !saved.favorites[item.Id]) { return false; }
            if (saved.group && (item.Tags || []).indexOf(saved.group) === -1) { return false; }
            return !q || [item.Name, item.Number, item.ChannelNumber, nowLine(item)].join(" ").toLowerCase().indexOf(q) !== -1;
        });
    }

    function setText(node, value) { if (node.textContent !== value) { node.textContent = value; } }

    function ensure() {
        var box = document.getElementById("firetv-live");
        if (box) { return box; }
        box = document.createElement("section");
        box.id = "firetv-live";
        box.className = "verticalSection";
        var head = document.createElement("div");
        head.className = "firetv-live-head";
        var title = document.createElement("div");
        title.className = "firetv-live-title";
        title.textContent = "Live TV";
        var count = document.createElement("div");
        count.className = "firetv-live-count";
        var search = document.createElement("input");
        search.type = "search";
        search.className = "firetv-live-search";
        search.placeholder = german() ? "Sender oder Sendung" : "Channel or show";
        search.setAttribute("aria-label", search.placeholder);
        search.value = filter;
        search.addEventListener("input", function () { filter = search.value; saved.scroll = 0; persist(); render(true); });
        head.appendChild(title); head.appendChild(count); head.appendChild(search);
        var controls = document.createElement("div");
        controls.className = "firetv-live-controls";
        [["all", german() ? "Alle Sender" : "All channels"], ["favorites", german() ? "Favoriten" : "Favorites"], ["recent", german() ? "Zuletzt gesehen" : "Recently watched"]].forEach(function (entry) {
            var button = document.createElement("button");
            button.type = "button"; button.textContent = entry[1]; button.setAttribute("data-mode", entry[0]);
            button.addEventListener("click", function () { saved.mode = entry[0]; saved.scroll = 0; persist(); render(true); });
            controls.appendChild(button);
        });
        var groups = document.createElement("select");
        groups.className = "firetv-live-groups";
        groups.setAttribute("aria-label", german() ? "Sendergruppe" : "Channel group");
        groups.addEventListener("change", function () { saved.group = groups.value; saved.scroll = 0; persist(); render(true); });
        controls.appendChild(groups);
        var status = document.createElement("div");
        status.className = "firetv-live-status";
        status.setAttribute("role", "status");
        viewport = document.createElement("div");
        viewport.className = "firetv-live-viewport";
        viewport.setAttribute("role", "list");
        viewport.setAttribute("aria-label", german() ? "Sender" : "Channels");
        content = document.createElement("div");
        content.className = "firetv-live-list";
        viewport.appendChild(content);
        viewport.addEventListener("scroll", function () {
            saved.scroll = viewport.scrollTop;
            if (!frame) { frame = requestAnimationFrame(function () { frame = 0; paintWindow(); }); }
        }, { passive: true });
        box.addEventListener("keydown", navigate, true);
        box.appendChild(head); box.appendChild(controls); box.appendChild(status); box.appendChild(viewport);
        // Hosted TV pages restore their own scroll positions and animate using
        // transforms. Keep the channel viewport outside those scrolling hosts.
        document.body.appendChild(box);
        rows = {};
        return box;
    }

    function updateGroups(box) {
        var select = box.querySelector(".firetv-live-groups");
        var tags = [];
        channels.forEach(function (item) { (item.Tags || []).forEach(function (tag) { if (tags.indexOf(tag) < 0) { tags.push(tag); } }); });
        tags.sort();
        var key = tags.join("|");
        if (select.getAttribute("data-groups") === key) { return; }
        select.setAttribute("data-groups", key);
        select.textContent = "";
        [""].concat(tags).forEach(function (tag) {
            var option = document.createElement("option"); option.value = tag;
            option.textContent = tag || (german() ? "Alle Gruppen" : "All groups");
            select.appendChild(option);
        });
        select.value = saved.group;
        if (select.selectedIndex < 0) { saved.group = ""; select.value = ""; }
    }

    function fitViewport() {
        if (!viewport) { return; }
        var header = document.querySelector(".skinHeader");
        var bottom = header ? header.getBoundingClientRect().bottom : 100;
        viewport.parentNode.style.top = Math.max(0, Math.min(bottom, window.innerHeight * 0.35)) + 12 + "px";
        var size = parseFloat(getComputedStyle(viewport).fontSize) || 24;
        rowHeight = Math.round(size * 6.5);
        viewport.style.height = Math.max(180, window.innerHeight - viewport.getBoundingClientRect().top - 36) + "px";
    }

    function render(reset) {
        var box = ensure();
        updateGroups(box);
        shown = visibleChannels();
        var grouped = shown.length && shown.every(isGroup);
        setText(box.querySelector(".firetv-live-title"), parentItem && parentItem.Name || "Live TV");
        setText(box.querySelector(".firetv-live-count"), shown.length + (grouped ? (german() ? " Gruppen" : " groups") : (german() ? " Sender" : " channels")));
        setText(box.querySelector(".firetv-live-status"), statusText || (loading ? (german() ? "Sender werden geladen…" : "Loading channels…") : ""));
        Array.prototype.forEach.call(box.querySelectorAll("[data-mode]"), function (button) {
            button.setAttribute("aria-pressed", String(button.getAttribute("data-mode") === saved.mode));
        });
        fitViewport();
        if (reset) { viewport.scrollTop = 0; }
        else if (!Object.keys(rows).length) { viewport.scrollTop = saved.scroll || 0; }
        paintWindow();
    }

    function paintWindow() {
        if (!viewport || !content) { return; }
        var height = viewport.clientHeight || 650;
        var maximum = Math.max(0, shown.length * rowHeight - height);
        if (viewport.scrollTop > maximum) { viewport.scrollTop = maximum; }
        var start = Math.max(0, Math.floor(viewport.scrollTop / rowHeight) - OVERSCAN);
        var end = Math.min(shown.length, start + Math.ceil(height / rowHeight) + OVERSCAN * 2);
        content.style.height = Math.max(shown.length * rowHeight, 1) + "px";
        var keep = {};
        for (var i = start; i < end; i++) {
            var item = shown[i];
            keep[item.Id] = true;
            var entry = rows[item.Id];
            if (!entry) { entry = buildLiveRow(item); rows[item.Id] = entry; content.appendChild(entry); }
            entry.style.top = (i * rowHeight) + "px";
            entry.style.height = rowHeight + "px";
            entry.setAttribute("data-index", i);
            entry.setAttribute("aria-posinset", i + 1);
            entry.setAttribute("aria-setsize", shown.length);
            updateRow(entry, item);
        }
        Object.keys(rows).forEach(function (id) { if (!keep[id]) { rows[id].remove(); delete rows[id]; } });
        var empty = viewport.querySelector(".firetv-live-empty");
        if (!shown.length && !empty) {
            empty = document.createElement("div"); empty.className = "firetv-live-empty";
            viewport.appendChild(empty);
        }
        if (empty) {
            if (shown.length) { empty.remove(); }
            else { setText(empty, loading ? (german() ? "Sender werden geladen…" : "Loading channels…") : (german() ? "Keine passenden Sender." : "No matching channels.")); }
        }
        scheduleGuide();
    }

    function buildLiveRow(item) {
        var entry = document.createElement("div"); entry.className = "firetv-live-entry";
        entry.setAttribute("role", "listitem"); entry.setAttribute("data-id", item.Id);
        var row = document.createElement("button"); row.type = "button"; row.className = "firetv-live-row";
        row.setAttribute("data-id", item.Id); row.setAttribute("data-type", isGroup(item) ? "Folder" : "TvChannel");
        var logo = document.createElement("div"); logo.className = "firetv-live-logo"; logo.setAttribute("aria-hidden", "true");
        logo.textContent = String(item.Name || "TV").slice(0, 2).toUpperCase();
        row.appendChild(logo);
        var copy = document.createElement("div"); copy.className = "firetv-live-copy";
        ["name", "now", "bar", "next"].forEach(function (kind) {
            var node = document.createElement("div"); node.className = "firetv-live-" + kind;
            if (kind === "bar") { node.appendChild(document.createElement("span")); node.setAttribute("role", "progressbar"); node.setAttribute("aria-valuemin", "0"); node.setAttribute("aria-valuemax", "100"); node.setAttribute("aria-label", german() ? "Sendungsfortschritt" : "Program progress"); }
            copy.appendChild(node);
        });
        row.appendChild(copy);
        row.addEventListener("focus", function () { saved.focusId = item.Id; persist(); });
        row.addEventListener("click", function (event) { event.preventDefault(); event.stopPropagation(); saved.lastId = item.Id; saved.scroll = viewport.scrollTop; persist(); play(byId[item.Id], shown); });
        var favorite = document.createElement("button"); favorite.type = "button"; favorite.className = "firetv-live-favorite";
        favorite.addEventListener("click", function () {
            if (saved.favorites[item.Id]) { delete saved.favorites[item.Id]; } else { saved.favorites[item.Id] = true; }
            persist();
            if (saved.mode === "favorites") { var index = Number(entry.getAttribute("data-index")); render(false); focusIndex(Math.min(index, shown.length - 1), true); }
            else { updateRow(entry, byId[item.Id]); }
        });
        entry.appendChild(row); entry.appendChild(favorite);
        return entry;
    }

    function updateRow(entry, item) {
        var number = item.Number || item.ChannelNumber;
        setText(entry.querySelector(".firetv-live-name"), (number ? number + "  " : "") + String(item.Name || "").split(/ {2}· {2}/)[0]);
        setText(entry.querySelector(".firetv-live-now"), isGroup(item) ? (item.Overview || "Gruppe öffnen") : nowLine(item) || (german() ? "Keine EPG-Daten" : "No guide data"));
        setText(entry.querySelector(".firetv-live-next"), nextLine(item));
        var percent = progressOf(item, Date.now());
        var bar = entry.querySelector(".firetv-live-bar");
        bar.style.visibility = percent == null ? "hidden" : "visible";
        if (percent != null) { bar.firstChild.style.width = percent + "%"; bar.setAttribute("aria-valuenow", Math.round(percent)); }
        var favorite = entry.querySelector(".firetv-live-favorite");
        favorite.hidden = isGroup(item);
        setText(favorite, saved.favorites[item.Id] ? "★" : "☆");
        favorite.setAttribute("aria-pressed", String(!!saved.favorites[item.Id]));
        favorite.setAttribute("aria-label", (saved.favorites[item.Id] ? (german() ? "Favorit entfernen: " : "Remove favorite: ") : (german() ? "Als Favorit speichern: " : "Add favorite: ")) + item.Name);
        var logo = entry.querySelector(".firetv-live-logo");
        var url = posterUrl(item);
        if (url && logo.getAttribute("data-url") !== url) {
            logo.setAttribute("data-url", url);
            var image = document.createElement("img"); image.alt = ""; image.setAttribute("loading", "lazy"); image.setAttribute("decoding", "async");
            image.addEventListener("error", function () { image.remove(); });
            var old = logo.querySelector("img"); if (old) { old.remove(); }
            logo.appendChild(image);
            if (window.FireTvLogos) { window.FireTvLogos.mount(image, stateKey + "|" + item.Id, url, auth().accessToken); }
            else { image.src = url; }
        }
    }

    function focusIndex(index, favorite) {
        if (index < 0 || index >= shown.length || !viewport) { return; }
        var height = viewport.clientHeight || 650;
        if (index * rowHeight < viewport.scrollTop) { viewport.scrollTop = index * rowHeight; }
        else if ((index + 1) * rowHeight > viewport.scrollTop + height) { viewport.scrollTop = (index + 1) * rowHeight - height; }
        paintWindow();
        var entry = rows[shown[index].Id];
        if (entry) { entry.querySelector(favorite && !isGroup(shown[index]) ? ".firetv-live-favorite" : ".firetv-live-row").focus({ preventScroll: true }); }
    }

    function navigate(event) {
        var entry = event.target.closest && event.target.closest(".firetv-live-entry");
        var key = event.key;
        if (!entry) {
            if (key === "ArrowDown" && event.target.closest(".firetv-live-head, .firetv-live-controls") && event.target.tagName !== "SELECT" && shown.length) {
                event.preventDefault(); event.stopImmediatePropagation(); focusIndex(Math.max(0, shown.findIndex(function (item) { return item.Id === saved.focusId; })), false);
            }
            return;
        }
        var index = Number(entry.getAttribute("data-index"));
        var favorite = event.target.classList.contains("firetv-live-favorite");
        if (key === "ArrowDown" || key === "ArrowUp") {
            event.preventDefault(); event.stopImmediatePropagation();
            if (key === "ArrowUp" && index === 0) { document.querySelector("#firetv-live .firetv-live-search").focus(); }
            else { focusIndex(index + (key === "ArrowDown" ? 1 : -1), favorite); }
        } else if (key === "ArrowRight" || key === "ArrowLeft") {
            event.preventDefault(); event.stopImmediatePropagation(); focusIndex(index, key === "ArrowRight");
        }
    }

    function scheduleGuide() {
        if (guideTimer) { return; }
        guideTimer = setTimeout(function () { guideTimer = 0; refreshGuide(); }, 350);
    }

    function refreshGuide() {
        if (!viewport || loading || guideLoading || document.hidden || paused) { return; }
        var ids = Object.keys(rows);
        var now = Date.now();
        ids = ids.filter(function (id) { return !isGroup(byId[id]) && (!programs[id] || programs[id].expires <= now); });
        if (!ids.length) { return; }
        var client = window.ApiClient;
        if (!client || !client.getJSON) { return; }
        guideLoading = true;
        var token = requestToken;
        client.getJSON(client.getUrl("LiveTv/Programs", {
            userId: client.getCurrentUserId(), channelIds: ids.join(","), minEndDate: new Date(now).toISOString(),
            maxStartDate: new Date(now + 12 * 3600000).toISOString(), sortBy: "StartDate", sortOrder: "Ascending",
            enableImages: false, enableUserData: false, enableTotalRecordCount: false, limit: 500
        })).then(function (result) {
            if (token !== requestToken || !viewport) { return; }
            var all = (result && result.Items) || [];
            ids.forEach(function (id) {
                var list = all.filter(function (program) { return program.ChannelId === id; }).sort(function (a, b) { return Date.parse(a.StartDate) - Date.parse(b.StartDate); });
                var current = list.filter(function (p) { return Date.parse(p.StartDate) <= now && Date.parse(p.EndDate) > now; })[0];
                var next = list.filter(function (p) { return Date.parse(p.StartDate) > now; })[0];
                var item = byId[id];
                if (item) {
                    if (current) { item.CurrentProgram = current; }
                    else if (item.CurrentProgram && Date.parse(item.CurrentProgram.EndDate) <= now) { delete item.CurrentProgram; }
                    if (next) { item.NextProgram = next; }
                    else if (item.NextProgram && Date.parse(item.NextProgram.EndDate) <= now) { delete item.NextProgram; }
                    if (rows[id]) { updateRow(rows[id], item); }
                }
                programs[id] = { expires: now + 60000 };
            });
        }).catch(function () {
            if (token === requestToken) { ids.forEach(function (id) { programs[id] = { expires: now + 30000 }; }); }
        }).then(function () { if (token === requestToken) { guideLoading = false; } });
    }

    function tick() {
        if (!viewport || document.hidden || paused) { return; }
        var now = Date.now();
        Object.keys(rows).forEach(function (id) { if (byId[id]) { updateRow(rows[id], byId[id]); } });
        refreshGuide();
        if (!loading && now >= nextChannelsRefresh) { refreshChannels(false); }
    }

    function refreshChannels(initial) {
        if (loading) { return; }
        loading = true;
        var token = requestToken;
        if (initial) { render(false); }
        fetchChannels(token).then(function (items) {
            if (token !== requestToken || !viewport) { return; }
            channels = items; byId = {};
            channels.forEach(function (item) { byId[item.Id] = item; });
            statusText = "";
            loading = false;
            nextChannelsRefresh = Date.now() + 5 * 60000;
            render(false);
            var active = document.activeElement;
            if (initial && shown.length && (!active || active === document.body ||
                (!active.closest("#firetv-live, .skinHeader") && active.closest(".libraryPage, .liveTvPage, .mainAnimatedPage")))) {
                var index = shown.findIndex(function (item) { return item.Id === (saved.focusId || saved.lastId); });
                var firstVisible = Math.ceil(viewport.scrollTop / rowHeight);
                if (index < firstVisible || (index + 1) * rowHeight > viewport.scrollTop + (viewport.clientHeight || 650)) { index = firstVisible; }
                var entry = shown[index] && rows[shown[index].Id];
                if (entry) { entry.querySelector(".firetv-live-row").focus({ preventScroll: true }); }
            }
        }).catch(function () {
            if (token !== requestToken || !viewport) { return; }
            loading = false;
            nextChannelsRefresh = Date.now() + 30000;
            statusText = german() ? "Server nicht erreichbar. Geladene Sender bleiben verfügbar; erneuter Versuch folgt." : "Server unavailable. Keeping loaded channels; retrying shortly.";
            render(false);
        });
    }

    function show(on) {
        document.documentElement.classList.toggle("firetv-live-on", on);
        if (document.body) { document.body.classList.toggle("firetv-live-on", on); }
    }

    function hide() {
        if (viewport) { saved.scroll = viewport.scrollTop; persist(); }
        requestToken++; loading = false; guideLoading = false; lastKey = "";
        clearInterval(timer); timer = 0; clearTimeout(guideTimer); guideTimer = 0;
        if (frame) { cancelAnimationFrame(frame); frame = 0; }
        var box = document.getElementById("firetv-live"); if (box) { box.remove(); }
        viewport = null; content = null; rows = {};
        show(false);
    }

    function sync() {
        var key = scopeKey() + "|" + hash();
        if (routeKey !== key) {
            routeKey = key;
            parentKey = ""; parentLive = false; parentItem = null; parentPending = false; parentToken++;
            channels = []; byId = {};
            hide();
        }
        // A previous page's heading and cards can stay mounted while the router
        // loads the next one. Only the API identity of this parent can own a list.
        var match = /^#\/?(?:list|items|library)\?/.test(hash()) && hash().match(/[?&]parentid=([a-f0-9]{32})(?:&|$)/);
        var client = window.ApiClient;
        if (match && parentKey !== key && !parentPending && client && client.getItem && client.getCurrentUserId()) {
            parentPending = true;
            var token = ++parentToken;
            client.getItem(client.getCurrentUserId(), match[1]).then(function (item) {
                if (token !== parentToken || key !== scopeKey() + "|" + hash()) { return; }
                parentKey = key; parentItem = item; parentLive = liveParent(item); parentPending = false;
                sync();
            }).catch(function () {
                if (token !== parentToken || key !== scopeKey() + "|" + hash()) { return; }
                parentKey = key; parentLive = false; parentPending = false;
                hide();
            });
        }
        if (!looksLikeLiveLibrary()) { hide(); return; }
        if (lastKey === key && viewport && viewport.isConnected) { return; }
        hide(); readState(); lastKey = key; show(true); ensure(); render(false);
        refreshChannels(true);
        timer = setInterval(tick, 15000);
    }

    function start() {
        ["pushState", "replaceState"].forEach(function (name) {
            var original = window.history[name];
            window.history[name] = function () {
                var result = original.apply(this, arguments);
                sync();
                return result;
            };
        });
        window.addEventListener("popstate", sync);
        window.addEventListener("hashchange", sync);
        window.addEventListener("resize", function () { if (viewport) { fitViewport(); paintWindow(); } });
        window.addEventListener("pagehide", persist);
        document.addEventListener("visibilitychange", function () { if (!document.hidden) { tick(); } else { persist(); } });
        var observer = new MutationObserver(function (records) {
            if (syncTimer) { return; }
            if (!records.some(function (record) {
                if (record.type === "attributes") { return record.target.matches && record.target.matches(".emby-tab-button"); }
                return !record.target.closest || !record.target.closest("#firetv-live");
            })) { return; }
            syncTimer = setTimeout(function () { syncTimer = 0; sync(); }, 400);
        });
        observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["class"] });
        sync();
    }

    window.FireTvLive = {
        sync: sync, play: play, progressOf: progressOf, nextLine: nextLine, clock: clock,
        setActive: function (active) {
            paused = !active;
            if (active) {
                tick();
                if (restoreAfterPlayback && viewport) {
                    restoreAfterPlayback = false;
                    var index = shown.findIndex(function (item) { return item.Id === saved.lastId; });
                    if (index >= 0) { focusIndex(index, false); }
                }
            }
        },
        onPlaybackState: function (state) {
            if (state && state.isLive && state.event === "playing" && state.itemId) { rememberPlayed(state.itemId); restoreAfterPlayback = true; }
        }
    };
    if (document.readyState === "loading") { document.addEventListener("DOMContentLoaded", start); } else { start(); }
})();
