/**
 * Fire TV cinema layer for hosted jellyfin-web.
 *
 * - Hides empty / failed home categories (including plugin "Failed to retrieve" rows)
 * - Does not hide official Live TV / Guide sections (few cards, Guide buttons)
 * - Restyles Live TV library-channel tiles as landscape now/next rows
 * - Smart-searches mixed libraries such as Treasure Maps (jellyfin-web skips those)
 * - Ranks SearchTerm results only — does not change 12.0 recursive-on-filter semantics
 *
 * Do not patch image element sources or ApiClient image URL helpers.
 * Do not add extra card outline rings.
 */
(function () {
    if (window.__firetvExperience) {
        return;
    }
    window.__firetvExperience = true;

    var FAILED = [
        "failed to retrieve",
        "failed to load",
        "request failed",
        "error retrieving",
        "section failed",
        "failed categor",
        "fehlgeschlagen",
        "konnte nicht geladen",
        "konnte nicht abgerufen",
        "fehler beim laden"
    ];
    var ARTICLES = ["the", "der", "die", "das", "ein", "eine", "a", "an", "le", "la", "les"];
    var hideTimer = 0;
    var searchTimer = 0;
    var lastQuery = "";
    var lastParentId = "";

    function $(selector, root) {
        return (root || document).querySelector(selector);
    }

    function all(selector, root) {
        return Array.prototype.slice.call((root || document).querySelectorAll(selector));
    }

    function normalize(value) {
        var text = String(value || "").toLowerCase().trim();
        text = text.replace(/ä/g, "ae").replace(/ö/g, "oe").replace(/ü/g, "ue").replace(/ß/g, "ss");
        for (var i = 0; i < ARTICLES.length; i++) {
            var prefix = ARTICLES[i] + " ";
            if (text.indexOf(prefix) === 0) {
                text = text.slice(prefix.length);
            }
        }
        return text.replace(/[^a-z0-9]+/g, " ").replace(/\s+/g, " ").trim();
    }

    function extractYear(value) {
        var match = String(value || "").match(/\b((?:19|20)\d{2})\b/);
        return match ? parseInt(match[1], 10) : null;
    }

    function queryVariants(raw) {
        var parsed = normalize(String(raw || "").replace(/\b((?:19|20)\d{2})\b/g, " "));
        var out = [];
        function add(value) {
            if (value && value.length >= 2 && out.indexOf(value) === -1) {
                out.push(value);
            }
        }
        add(String(raw || "").trim());
        add(parsed);
        add(parsed.replace(/ae/g, "ä").replace(/oe/g, "ö").replace(/ue/g, "ü"));
        return out;
    }

    function looksFailed(text) {
        var sample = String(text || "").toLowerCase();
        if (!sample) {
            return false;
        }
        for (var i = 0; i < FAILED.length; i++) {
            if (sample.indexOf(FAILED[i]) !== -1) {
                return true;
            }
        }
        return false;
    }

    function scoreItem(item, rawQuery, preferredParent) {
        var query = normalize(String(rawQuery || "").replace(/\b((?:19|20)\d{2})\b/g, " "));
        var year = extractYear(rawQuery);
        var name = normalize(item.Name || item.OriginalTitle || "");
        if (!name || !query) {
            return 0;
        }
        var tokens = query.split(" ").filter(function (token) { return token.length >= 2; });
        var points = 8;
        if (name === query) {
            points = 100;
        } else if (name.indexOf(query) === 0) {
            points = 82;
        } else if (name.indexOf(query) !== -1) {
            points = 64;
        } else if (tokens.length && tokens.every(function (token) { return name.indexOf(token) !== -1; })) {
            points = 58;
        } else {
            var hits = tokens.filter(function (token) { return name.indexOf(token) !== -1; }).length;
            if (hits) {
                points = 28 + (12 * hits);
            }
        }
        if (year && item.ProductionYear === year) {
            points += 16;
        }
        if (preferredParent && item.ParentId === preferredParent) {
            points += 22;
        }
        var type = String(item.Type || "").toLowerCase();
        if (type === "movie" || type === "series" || type === "boxset") {
            points += 10;
        } else if (type === "tvchannel" || type === "livetvprogram" || type === "program") {
            points += 8;
        } else if (type === "folder" || type === "collectionfolder") {
            points -= 6;
        } else if (type === "episode" || type === "season") {
            points -= 4;
        }
        if (item.CommunityRating > 0) {
            points += Math.min(10, item.CommunityRating);
        }
        return points;
    }

    function hashParams() {
        var hash = String(location.hash || "");
        var query = hash.split("?")[1] || "";
        var params = {};
        query.split("&").forEach(function (part) {
            var pair = part.split("=");
            if (pair[0]) {
                params[decodeURIComponent(pair[0]).toLowerCase()] = decodeURIComponent((pair[1] || "").replace(/\+/g, " "));
            }
        });
        return params;
    }

    function currentParentId() {
        var params = hashParams();
        var fromHash = params.parentid || params.parentId || params.topparentid;
        if (fromHash) {
            lastParentId = fromHash;
            try {
                sessionStorage.setItem("firetvParentId", fromHash);
            } catch (e) { /* private mode */ }
            return fromHash;
        }
        if (lastParentId) {
            return lastParentId;
        }
        try {
            return sessionStorage.getItem("firetvParentId") || "";
        } catch (e) {
            return "";
        }
    }

    function isSearchPage() {
        var hash = String(location.hash || "").toLowerCase();
        return hash.indexOf("search") !== -1 || !!$(".searchFields, .searchfields-input, input[type='search']");
    }

    function isHomePage() {
        var hash = String(location.hash || "").toLowerCase();
        return !hash || hash === "#" || hash === "#/" || hash.indexOf("home") !== -1 || !!$(".homeSectionsContainer, .homePage");
    }

    function isDetailPage() {
        var hash = String(location.hash || "").toLowerCase();
        return hash.indexOf("details") !== -1 || hash.indexOf("item") !== -1;
    }

    function isLiveTvPage() {
        var hash = String(location.hash || "").toLowerCase();
        return hash.indexOf("livetv") !== -1 ||
            hash.indexOf("live-tv") !== -1 ||
            !!$(".liveTvPage, .liveTvGuidePage, .channelsPage, .guidePage");
    }

    function pageHeading() {
        var node = $(".libraryPage .pageTitle, .headerTitle, h1, .sectionTitle");
        return node ? String(node.textContent || "").replace(/\s+/g, " ").trim() : "";
    }

    function isLiveTvContext() {
        if (isLiveTvPage()) {
            return true;
        }
        return /live[\s-]?tv|live tv/i.test(pageHeading());
    }

    function isLiveTvProtectedSection(section) {
        if (isLiveTvPage()) {
            return true;
        }
        var title = section.querySelector(".sectionTitle, .sectionTitleTextButton, h2");
        var text = ((title && title.textContent) || "").replace(/\s+/g, " ").trim().toLowerCase();
        if (/live[\s-]?tv|live tv|sender|channels|guide|epg|programm/.test(text)) {
            return true;
        }
        return !!(section.querySelector(".btnGuide, .guideButton, a[href*='livetv'], .programCell, .guide-channelHeaderCell, .channelPrograms"));
    }

    function cardPlainText(card) {
        return String(card.textContent || "").replace(/\s+/g, " ").trim();
    }

    function parseLabeled(text, labels) {
        var lines = String(text || "").split(/\r?\n/);
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            for (var j = 0; j < labels.length; j++) {
                if (line.toLowerCase().indexOf(labels[j].toLowerCase()) === 0) {
                    var rest = line.slice(labels[j].length).trim();
                    if (!rest) {
                        continue;
                    }
                    var range = "";
                    var match = rest.match(/\(([^)]+)\)\s*$/);
                    if (match) {
                        range = match[1];
                        rest = rest.slice(0, match.index).trim();
                    }
                    return { title: rest, range: range };
                }
            }
        }
        return null;
    }

    function parseLiveTvCard(card) {
        var firstNode = card.querySelector(".cardText-first, .cardText, .firetv-card-title");
        var secondNode = card.querySelector(".cardText-secondary, .firetv-card-meta");
        var first = ((firstNode && firstNode.textContent) || "").replace(/\s+/g, " ").trim();
        var second = ((secondNode && secondNode.textContent) || "").trim();
        var blob = cardPlainText(card);
        var type = String(card.getAttribute("data-type") || "").toLowerCase();
        var dotted = first.split("  ·  ");
        var jetzt = parseLabeled(second, ["Jetzt:", "Now:"]) || parseLabeled(blob, ["Jetzt:", "Now:"]);
        var danach = parseLabeled(second, ["Danach:", "Next:"]) || parseLabeled(blob, ["Danach:", "Next:"]);
        var sender = blob.match(/\b(\d+)\s+Sender\b/i);
        var folder = !!(sender && !jetzt);
        var looksTyped = type === "tvchannel" || type === "livetvprogram" || type === "program";
        var looksGuide = !!(jetzt || danach || folder);
        var looksDotted = dotted.length === 2 && dotted[0] && dotted[1];
        if (!looksTyped && !looksGuide && !looksDotted) {
            return null;
        }
        var channel = dotted[0] || first;
        var now = (jetzt && jetzt.title) || (folder ? "" : (dotted[1] || ""));
        if (folder && dotted[1] && /^\d+$/.test(dotted[1].trim())) {
            now = "";
        }
        return {
            channel: channel,
            now: now,
            nowRange: (jetzt && jetzt.range) || "",
            next: (danach && danach.title) || "",
            nextRange: (danach && danach.range) || "",
            folderLabel: sender ? sender[0] : "",
            isFolder: folder
        };
    }

    function looksLiveTvCard(card) {
        var type = String(card.getAttribute("data-type") || "").toLowerCase();
        if (type === "tvchannel" || type === "livetvprogram" || type === "program") {
            return true;
        }
        var text = cardPlainText(card);
        if (/Jetzt:|Danach:|\bNow:|\bNext:|\b\d+\s+Sender\b/i.test(text)) {
            return true;
        }
        return isLiveTvContext() && text.indexOf("  ·  ") !== -1;
    }

    function germanUi() {
        var lang = String((document.documentElement && document.documentElement.lang) || navigator.language || "").toLowerCase();
        return lang.indexOf("de") === 0;
    }

    function restyleLiveTvCards() {
        if (window.FireTvLive && typeof window.FireTvLive.sync === "function") {
            window.FireTvLive.sync();
        }
    }

    function hideFailedSections() {
        var roots = all(".homeSectionsContainer, .homePage, .modularHome, .sections, .searchResults, .searchResultsContainer");
        if (!roots.length && isHomePage()) {
            roots = [document.body];
        }
        roots.forEach(function (root) {
            all(".verticalSection, .homeSection, .customHomeSection", root).forEach(function (section) {
                if (section.id === "firetv-smart-results") {
                    return;
                }
                if (isLiveTvProtectedSection(section)) {
                    section.removeAttribute("data-firetv-hidden");
                    return;
                }
                var busy = section.querySelector(".busy, .loading, .progressring, .emby-progress, paper-spinner-lite");
                if (busy) {
                    return;
                }
                var cards = section.querySelectorAll(".card, .posterItem, .firetv-card");
                var title = section.querySelector(".sectionTitle, .sectionTitleTextButton, h2");
                var text = ((title && title.textContent) || "").replace(/\s+/g, " ").trim();
                var bodyText = (section.textContent || "").replace(/\s+/g, " ").trim().slice(0, 200);
                var failed = looksFailed(text) || looksFailed(bodyText);
                var empty = cards.length === 0 && !!title;
                if (failed || (empty && (isHomePage() || isSearchPage()) && bodyText.length < 240)) {
                    section.setAttribute("data-firetv-hidden", "1");
                } else if (section.getAttribute("data-firetv-hidden") === "1" && cards.length) {
                    section.removeAttribute("data-firetv-hidden");
                }
            });
        });
        restyleLiveTvCards();
    }

    function scheduleHide() {
        window.clearTimeout(hideTimer);
        hideTimer = window.setTimeout(hideFailedSections, 280);
    }

    function originalGetItems(client) {
        return client.__firetvOrigGetItems || client.getItems.bind(client);
    }

    function wrapApiClient() {
        var client = window.ApiClient;
        if (!client || typeof client.getItems !== "function" || client.__firetvGetItemsWrapped) {
            return !!client;
        }
        client.__firetvOrigGetItems = client.getItems.bind(client);
        client.__firetvGetItemsWrapped = true;
        client.getItems = function (userId, options) {
            var uid = userId;
            var opts = options;
            if (userId && typeof userId === "object" && options === undefined) {
                opts = userId;
                uid = client.getCurrentUserId && client.getCurrentUserId();
            }
            opts = opts || {};
            if (!opts.SearchTerm) {
                return client.__firetvOrigGetItems.apply(client, arguments);
            }
            var preferred = currentParentId();
            var local = {};
            Object.keys(opts).forEach(function (key) { local[key] = opts[key]; });
            // 12.0 already applies Recursive with filters + includeItemTypes; keep it on for search ranking only.
            local.Recursive = true;
            if (!local.ParentId && preferred && isSearchPage()) {
                local.ParentId = preferred;
            }
            return client.__firetvOrigGetItems.call(client, uid, local).then(function (result) {
                var items = (result && result.Items) || [];
                if (items.length >= 8 || !preferred) {
                    return rankResult(result, items, opts.SearchTerm, preferred);
                }
                var wide = {};
                Object.keys(local).forEach(function (key) { wide[key] = local[key]; });
                delete wide.ParentId;
                return client.__firetvOrigGetItems.call(client, uid, wide).then(function (globalResult) {
                    return rankResult(result || globalResult, mergeItems(items, (globalResult && globalResult.Items) || []), opts.SearchTerm, preferred);
                });
            });
        };
        return true;
    }

    function mergeItems(left, right) {
        var seen = {};
        var out = [];
        function add(item) {
            if (!item || !item.Id || seen[item.Id]) {
                return;
            }
            seen[item.Id] = true;
            out.push(item);
        }
        (left || []).forEach(add);
        (right || []).forEach(add);
        return out;
    }

    function rankResult(result, items, query, preferred) {
        var ranked = (items || []).slice().sort(function (a, b) {
            return scoreItem(b, query, preferred) - scoreItem(a, query, preferred);
        });
        var copy = result ? JSON.parse(JSON.stringify(result)) : { Items: [], TotalRecordCount: 0 };
        copy.Items = ranked;
        copy.TotalRecordCount = ranked.length;
        return copy;
    }

    function posterUrl(item) {
        var client = window.ApiClient;
        if (!client || !item || !item.Id) {
            return "";
        }
        if (item.ImageTags && item.ImageTags.Primary && typeof client.getImageUrl === "function") {
            return client.getImageUrl(item.Id, {
                type: "Primary",
                tag: item.ImageTags.Primary,
                maxHeight: 540
            });
        }
        return "";
    }

    function openItem(item) {
        if (window.Emby && window.Emby.Page && typeof window.Emby.Page.showItem === "function") {
            window.Emby.Page.showItem(item);
            return;
        }
        location.hash = "#/details?id=" + encodeURIComponent(item.Id);
    }

    function ensureOverlay() {
        var host = $(".searchResults, .searchResultsContainer, .page.searchPage, .mainAnimatedPage") || $(".padded-right.padded-bottom-page") || document.body;
        var existing = $("#firetv-smart-results");
        if (existing) {
            return existing;
        }
        var box = document.createElement("div");
        box.id = "firetv-smart-results";
        box.className = "verticalSection";
        if (host.firstChild) {
            host.insertBefore(box, host.firstChild);
        } else {
            host.appendChild(box);
        }
        return box;
    }

    function renderOverlay(items, query) {
        var box = ensureOverlay();
        box.innerHTML = "";
        if (!items.length) {
            box.setAttribute("data-firetv-hidden", "1");
            return;
        }
        box.removeAttribute("data-firetv-hidden");
        var title = document.createElement("div");
        title.className = "firetv-smart-title";
        var lang = String((document.documentElement && document.documentElement.lang) || navigator.language || "").toLowerCase();
        title.textContent = lang.indexOf("de") === 0 ? "Beste Treffer" : "Top matches";
        var row = document.createElement("div");
        row.className = "firetv-smart-row";
        items.slice(0, 24).forEach(function (item) {
            var card = document.createElement("button");
            card.type = "button";
            card.className = "firetv-card card";
            if (item.Type) {
                card.setAttribute("data-type", item.Type);
            }
            card.setAttribute("data-id", item.Id);
            card.tabIndex = 0;
            var poster = document.createElement("div");
            poster.className = "firetv-poster cardImageContainer";
            var url = posterUrl(item);
            if (url) {
                poster.style.backgroundImage = "url('" + url.replace(/'/g, "%27") + "')";
            }
            var name = document.createElement("div");
            name.className = "firetv-card-title";
            var live = !!(item.IsLiveStream || item.Type === "TvChannel" || item.Type === "LiveTvProgram" || item.Type === "Program" ||
                (item.Overview && /Jetzt:|Danach:|\bNow:|\bNext:/.test(item.Overview)));
            if (live) {
                card.setAttribute("data-firetv-livetv", "1");
            }
            name.textContent = live
                ? (item.OriginalTitle || String(item.Name || "").split("  ·  ")[0] || item.Name || "")
                : (item.Name || "");
            var meta = document.createElement("div");
            meta.className = "firetv-card-meta";
            var bits = [];
            if (live && item.Overview) {
                var nowHit = String(item.Overview).split(/\r?\n/).filter(function (line) {
                    return /Jetzt:|Now:/i.test(line);
                })[0];
                if (nowHit) {
                    bits.push(nowHit.trim());
                }
            }
            if (item.ProductionYear) {
                bits.push(String(item.ProductionYear));
            }
            if (item.CommunityRating) {
                bits.push("★ " + Number(item.CommunityRating).toFixed(1));
            }
            if (item.OfficialRating) {
                bits.push(item.OfficialRating);
            }
            meta.textContent = bits.join("  ·  ");
            card.appendChild(poster);
            card.appendChild(name);
            card.appendChild(meta);
            card.addEventListener("focus", function () { card.classList.add("focused"); });
            card.addEventListener("blur", function () { card.classList.remove("focused"); });
            card.addEventListener("click", function (event) {
                event.preventDefault();
                openItem(item);
            });
            row.appendChild(card);
        });
        box.appendChild(title);
        box.appendChild(row);
        hideDuplicateCards(items);
    }

    function hideDuplicateCards(items) {
        var ids = {};
        items.forEach(function (item) { ids[item.Id] = true; });
        all(".card[data-id]").forEach(function (card) {
            if (card.closest("#firetv-smart-results")) {
                return;
            }
            if (ids[card.getAttribute("data-id")]) {
                card.setAttribute("data-firetv-hidden", "1");
            }
        });
    }

    function fetchSmart(query) {
        var client = window.ApiClient;
        if (!client || typeof client.getItems !== "function") {
            return Promise.resolve([]);
        }
        var uid = client.getCurrentUserId && client.getCurrentUserId();
        var parent = currentParentId();
        var getItems = originalGetItems(client);
        var fields = "PrimaryImageAspectRatio,ProductionYear,CommunityRating,OfficialRating,ProviderIds,Overview,OriginalTitle,Genres,RunTimeTicks,ParentId";
        var requests = [];
        queryVariants(query).forEach(function (term) {
            var base = {
                Recursive: true,
                SearchTerm: term,
                Limit: 40,
                Fields: fields,
                IncludeItemTypes: "Movie,Series,BoxSet,Video,Folder,TvChannel,LiveTvProgram,MusicAlbum,Audio",
                EnableTotalRecordCount: false
            };
            if (parent) {
                var scoped = {};
                Object.keys(base).forEach(function (key) { scoped[key] = base[key]; });
                scoped.ParentId = parent;
                requests.push(getItems.call(client, uid, scoped));
            }
            requests.push(getItems.call(client, uid, base));
        });
        return Promise.all(requests.map(function (promise) {
            return promise.catch(function () { return { Items: [] }; });
        })).then(function (results) {
            var merged = [];
            results.forEach(function (result) {
                merged = mergeItems(merged, (result && result.Items) || []);
            });
            return merged.sort(function (a, b) {
                return scoreItem(b, query, parent) - scoreItem(a, query, parent);
            });
        });
    }

    function runSmartSearch(query) {
        query = String(query || "").trim();
        lastQuery = query;
        if (query.length < 2) {
            var box = $("#firetv-smart-results");
            if (box) {
                box.setAttribute("data-firetv-hidden", "1");
                box.innerHTML = "";
            }
            scheduleHide();
            return;
        }
        fetchSmart(query).then(function (items) {
            if (query !== lastQuery) {
                return;
            }
            renderOverlay(items, query);
            scheduleHide();
        });
    }

    function searchInput() {
        return $("input[type='search'], input.searchfields-txtSearch, .searchFields input, .searchfields-header input, .searchfields-input");
    }

    function bindSearch() {
        var input = searchInput();
        if (!input || input.__firetvBound) {
            return;
        }
        input.__firetvBound = true;
        input.addEventListener("input", function () {
            window.clearTimeout(searchTimer);
            var value = input.value;
            searchTimer = window.setTimeout(function () { runSmartSearch(value); }, 320);
        });
        if (input.value && input.value.length >= 2) {
            runSmartSearch(input.value);
        }
    }

    function formatRuntime(ticks) {
        if (!ticks) {
            return "";
        }
        var minutes = Math.round(ticks / 600000000);
        if (minutes < 1) {
            return "";
        }
        var hours = Math.floor(minutes / 60);
        var rest = minutes % 60;
        if (!hours) {
            return rest + "m";
        }
        return hours + "h " + (rest < 10 ? "0" : "") + rest + "m";
    }

    function paintDetails(item) {
        if (!item || $("#firetv-imdb-meta")) {
            return;
        }
        var host = $(".nameContainer, .itemName, .detailPagePrimaryContainer, .mainAnimatedPage");
        if (!host) {
            return;
        }
        var row = document.createElement("div");
        row.id = "firetv-imdb-meta";
        row.className = "firetv-imdb";
        if (item.CommunityRating) {
            var star = document.createElement("span");
            star.className = "firetv-imdb-star";
            star.textContent = "★ " + Number(item.CommunityRating).toFixed(1);
            row.appendChild(star);
        }
        if (item.ProductionYear) {
            var year = document.createElement("span");
            year.textContent = String(item.ProductionYear);
            row.appendChild(year);
        }
        var runtime = formatRuntime(item.RunTimeTicks);
        if (runtime) {
            var time = document.createElement("span");
            time.textContent = runtime;
            row.appendChild(time);
        }
        if (item.OfficialRating) {
            var chip = document.createElement("span");
            chip.className = "firetv-imdb-chip";
            chip.textContent = item.OfficialRating;
            row.appendChild(chip);
        }
        (item.Genres || []).slice(0, 3).forEach(function (genre) {
            var tag = document.createElement("span");
            tag.className = "firetv-imdb-chip";
            tag.textContent = genre;
            row.appendChild(tag);
        });
        var anchor = $(".itemName, .nameContainer");
        if (anchor && anchor.parentNode) {
            anchor.parentNode.insertBefore(row, anchor.nextSibling);
        } else {
            host.insertBefore(row, host.firstChild);
        }
        if (item.Overview && !$(".overview, .detail-clamp-text, .itemOverview, .firetv-imdb-plot")) {
            var plot = document.createElement("div");
            plot.className = "firetv-imdb-plot";
            plot.textContent = item.Overview;
            row.parentNode.insertBefore(plot, row.nextSibling);
        }
    }

    function enhanceDetails() {
        if (!isDetailPage() || !window.ApiClient) {
            return;
        }
        var params = hashParams();
        var id = params.id || params.itemid;
        if (!id || window.__firetvDetailId === id) {
            return;
        }
        window.__firetvDetailId = id;
        var client = window.ApiClient;
        var uid = client.getCurrentUserId && client.getCurrentUserId();
        if (typeof client.getItem !== "function") {
            return;
        }
        client.getItem(uid, id).then(paintDetails).catch(function () { /* keep stock page */ });
    }

    function tick() {
        currentParentId();
        wrapApiClient();
        bindSearch();
        if (isSearchPage()) {
            var input = searchInput();
            if (input && input.value && input.value !== lastQuery) {
                runSmartSearch(input.value);
            }
        }
        if (isDetailPage()) {
            enhanceDetails();
        }
        scheduleHide();
    }

    function start() {
        wrapApiClient();
        tick();
        window.addEventListener("hashchange", function () {
            window.__firetvDetailId = "";
            tick();
        });
        var observer = new MutationObserver(function () {
            wrapApiClient();
            bindSearch();
            scheduleHide();
            if (isDetailPage()) {
                enhanceDetails();
            }
        });
        if (document.documentElement) {
            observer.observe(document.documentElement, { childList: true, subtree: true });
        }
        window.setInterval(function () {
            wrapApiClient();
            bindSearch();
        }, 4000);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start);
    } else {
        start();
    }
})();
