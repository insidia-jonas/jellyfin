/* Live TV overview for jellyfin-web (and the in-repo Fire TV web host).
   Replaces the poster wall with a Kodi-PVR-style list: logo, sender, Jetzt +
   times, progress of the current show, optional Danach. Group folders stay
   folders; opening one uses this same list. Treasure-Maps pages are ignored. */
(function () {
    'use strict';

    if (window.JellyfinLiveTvOverview && window.JellyfinLiveTvOverview.__bound) {
        return;
    }

    var items = [];
    var parentItem = null;
    var filter = '';
    var loading = false;
    var loadError = '';
    var lastKey = '';
    var loadGen = 0;
    var owned = false;
    var tickTimer = 0;
    var refreshTimer = 0;
    var syncTimer = 0;
    var paintToken = 0;
    var emptyRetry = 0;
    var EMPTY_RETRY_MS = [1500, 3000, 6000, 12000];
    var ROW_CHUNK = 24;

    var style = document.createElement('style');
    style.setAttribute('data-livetv-overview', '1');
    style.textContent =
        'html.jf-livetv-list-on .libraryPage .itemsContainer,' +
        'html.jf-livetv-list-on .liveTvPage .itemsContainer,' +
        'html.jf-livetv-list-on .alphaPicker{display:none!important}' +
        'html.jf-livetv-list-on .loading,' +
        'html.jf-livetv-list-on .docspinner,' +
        'html.jf-livetv-list-on .busyIndicator,' +
        'html.jf-livetv-list-on paper-spinner-lite,' +
        'html.jf-livetv-list-on .mdl-spinner,' +
        'html.jf-livetv-list-on .progressring,' +
        'html.jf-livetv-list-on .emby-progress,' +
        'html.jf-livetv-list-on .busy{display:none!important;visibility:hidden!important}' +
        '#jf-livetv-overview{margin:.6em 1.2em 2em;max-width:76rem;box-sizing:border-box;position:relative;z-index:2}' +
        '#jf-livetv-overview .jf-livetv-head{display:flex;flex-wrap:wrap;align-items:center;gap:.7em 1.1em;margin:0 0 .85em}' +
        '#jf-livetv-overview .jf-livetv-title{font-size:1.55em;font-weight:750;letter-spacing:.02em}' +
        '#jf-livetv-overview .jf-livetv-count{opacity:.72;font-weight:600}' +
        '#jf-livetv-overview .jf-livetv-search{min-width:16em;padding:.5em .85em;border:0;border-radius:10px;' +
        'background:rgba(255,255,255,.08);color:inherit;font:inherit}' +
        '#jf-livetv-overview .jf-livetv-cols{display:none;grid-template-columns:4.6em minmax(8rem,16rem) minmax(14rem,1fr) minmax(10rem,18rem);' +
        'gap:1em;padding:.35em .8em;opacity:.55;font-size:.82em;font-weight:700;letter-spacing:.04em;text-transform:uppercase}' +
        '#jf-livetv-overview .jf-livetv-list{display:flex;flex-direction:column;gap:.45em}' +
        '#jf-livetv-overview .jf-livetv-row{display:grid;grid-template-columns:4.6em minmax(8rem,16rem) minmax(14rem,1fr) minmax(10rem,18rem);' +
        'align-items:center;gap:1em;width:100%;min-height:5.6em;padding:.55em .8em;border:1px solid rgba(255,255,255,.1);' +
        'border-radius:12px;background:rgba(255,255,255,.06);color:inherit;text-align:left;font:inherit;cursor:pointer;outline:none}' +
        '#jf-livetv-overview .jf-livetv-row:hover{background:rgba(255,255,255,.11)}' +
        '#jf-livetv-overview .jf-livetv-row:focus{background:rgba(10,132,255,.18);border-color:rgba(10,132,255,.7)}' +
        '#jf-livetv-overview .jf-livetv-logo{width:4.6em;height:4.6em;border-radius:8px;background:rgba(0,0,0,.35) center/contain no-repeat}' +
        '#jf-livetv-overview .jf-livetv-sender{font-size:1.12em;font-weight:750;line-height:1.25;overflow-wrap:anywhere}' +
        '#jf-livetv-overview .jf-livetv-now{min-width:0}' +
        '#jf-livetv-overview .jf-livetv-show{font-weight:650;line-height:1.3;overflow-wrap:anywhere}' +
        '#jf-livetv-overview .jf-livetv-times{margin:.15em 0 .4em;opacity:.78;font-weight:600;font-size:.92em}' +
        '#jf-livetv-overview .jf-livetv-bar{height:8px;border-radius:99px;background:rgba(255,255,255,.12);overflow:hidden}' +
        '#jf-livetv-overview .jf-livetv-bar>span{display:block;height:100%;width:0;background:#0a84ff;border-radius:99px}' +
        '#jf-livetv-overview .jf-livetv-next{opacity:.78;font-size:.95em;line-height:1.35;overflow-wrap:anywhere}' +
        '#jf-livetv-overview .jf-livetv-empty{opacity:.75;font-size:1.05em;padding:1.1em .2em}' +
        '@media (min-width:900px){#jf-livetv-overview .jf-livetv-cols{display:grid}}' +
        '.layout-tv #jf-livetv-overview .jf-livetv-row{min-height:6.8em;padding:.7em 1em}' +
        '.layout-tv #jf-livetv-overview .jf-livetv-sender{font-size:1.28em}' +
        '.layout-tv #jf-livetv-overview .jf-livetv-show{font-size:1.08em}' +
        '@media (max-width:820px){' +
        '#jf-livetv-overview .jf-livetv-cols{display:none}' +
        '#jf-livetv-overview .jf-livetv-row{grid-template-columns:4.2em 1fr;grid-template-areas:"logo sender" "logo now" "logo next"}' +
        '#jf-livetv-overview .jf-livetv-logo{grid-area:logo}' +
        '#jf-livetv-overview .jf-livetv-sender{grid-area:sender}' +
        '#jf-livetv-overview .jf-livetv-now{grid-area:now}' +
        '#jf-livetv-overview .jf-livetv-next{grid-area:next}' +
        '}';
    document.head.appendChild(style);

    function german() {
        var lang = String((document.documentElement && document.documentElement.lang) || navigator.language || '').toLowerCase();
        return lang.indexOf('de') === 0;
    }

    function api() {
        return window.ApiClient;
    }

    function hash() {
        return String(location.hash || '');
    }

    function hashLower() {
        return hash().toLowerCase();
    }

    function pageTitle() {
        var node = document.querySelector('.libraryPage .pageTitle, .headerTitle, h1, .sectionTitle');
        return node ? String(node.textContent || '').replace(/\s+/g, ' ').trim() : '';
    }

    function isGuidePage() {
        return hashLower().indexOf('guide') !== -1;
    }

    function isOfficialLiveHash() {
        if (isGuidePage() || isLiveTvGroupHash()) {
            return false;
        }
        var path = hashLower().split('?')[0];
        return path.indexOf('livetv') !== -1 || path.indexOf('live-tv') !== -1;
    }

    function isLiveTvGroupHash() {
        return /[?&]ltvgroup=1(?:&|$)/i.test(hash());
    }

    function parentIdFromHash() {
        var match = hash().match(/[?&]parentId=([a-f0-9]{32})/i);
        return match ? match[1] : '';
    }

    function hideLibrarySpinner() {
        var nodes = document.querySelectorAll(
            '.loading, .docspinner, .busyIndicator, paper-spinner-lite, .mdl-spinner, .progressring, .emby-progress, .busy'
        );
        var i;
        for (i = 0; i < nodes.length; i++) {
            nodes[i].classList.add('hide');
            nodes[i].style.display = 'none';
        }
        if (window.loading && typeof window.loading.hide === 'function') {
            try {
                window.loading.hide();
            } catch (e) { /* ignore */ }
        }
    }

    function looksTreasureMaps(item) {
        if (!item) {
            return false;
        }
        if (item.ProviderIds && (item.ProviderIds.TreasureMaps || item.ProviderIds.treasuremaps)) {
            return true;
        }
        var name = String(item.Name || item.ChannelName || '');
        return /treasure[\s-]?maps/i.test(name);
    }

    function isGroupFolder(item) {
        if (!item || looksTreasureMaps(item)) {
            return false;
        }
        var ext = String(item.ExternalId || item.SourceId || '');
        if (ext.indexOf('g:') === 0) {
            return true;
        }
        var folder = !!(item.IsFolder || item.Type === 'Folder' || item.Type === 'ChannelFolderItem');
        if (!folder) {
            return false;
        }
        if (item.ProviderIds && (item.ProviderIds.LiveTv || item.ProviderIds.livetv)) {
            return true;
        }
        return /\d+\s+Sender/i.test(String(item.Overview || ''));
    }

    function isLiveTvItem(item) {
        if (!item || looksTreasureMaps(item)) {
            return false;
        }
        if (item.Type === 'TvChannel' || item.Type === 'LiveTvChannel' || item.CurrentProgram || item.NextProgram) {
            return true;
        }
        if (item.ProviderIds && (item.ProviderIds.LiveTv || item.ProviderIds.livetv)) {
            return true;
        }
        if (isGroupFolder(item)) {
            return true;
        }
        var text = [item.OriginalTitle, item.Overview, item.Name].join('\n');
        if (/Jetzt:|Danach:|\bNow:|\bNext:/.test(text)) {
            return true;
        }
        var tags = item.Tags || [];
        return !!(item.IsLiveStream || tags.indexOf('livestream') >= 0) && !!(item.StartDate || item.PremiereDate || item.EndDate);
    }

    function isLiveTvParent(item) {
        if (!item || looksTreasureMaps(item)) {
            return false;
        }
        if (item.Name === 'Live TV' || item.Name === 'Live-TV') {
            return item.Type === 'Channel' || !!item.ChannelId || item.Type === 'Folder';
        }
        return isGroupFolder(item);
    }

    function clock(iso) {
        if (!iso) {
            return '';
        }
        var value = Date.parse(iso);
        if (!value) {
            var time = String(iso).split('T')[1] || '';
            return time.slice(0, 5);
        }
        var date = new Date(value);
        var hours = date.getHours();
        var minutes = date.getMinutes();
        return (hours < 10 ? '0' : '') + hours + ':' + (minutes < 10 ? '0' : '') + minutes;
    }

    function rangeText(start, end) {
        var a = clock(start);
        var b = clock(end);
        if (a && b) {
            return a + '–' + b;
        }
        return a || b || '';
    }

    function parseLabeled(text, labels) {
        var lines = String(text || '').replace(/\r\n/g, '\n').replace(/ {2}· {2}/g, '\n').split('\n');
        var i;
        for (i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            var n;
            for (n = 0; n < labels.length; n++) {
                if (line.toLowerCase().indexOf(labels[n]) === 0) {
                    var rest = line.slice(labels[n].length).trim();
                    var match = rest.match(/^(.*)\s+\(([^)]+)\)\s*$/);
                    if (match) {
                        return { title: match[1].trim(), range: match[2] };
                    }
                    return { title: rest, range: '' };
                }
            }
        }
        return null;
    }

    function guideOf(item) {
        var program = item.CurrentProgram || {};
        var next = item.NextProgram || {};
        var blob = [item.OriginalTitle, item.Overview].join('\n');
        var nowHit = parseLabeled(blob, ['jetzt:', 'now:']);
        var nextHit = parseLabeled(blob, ['danach:', 'next:']);
        var start = program.StartDate || item.StartDate || item.PremiereDate;
        var end = program.EndDate || item.EndDate;
        return {
            channelName: item.Name || '',
            nowTitle: program.Name || (item.ProviderIds && (item.ProviderIds.LiveTvNow || item.ProviderIds.livetvnow)) || (nowHit && nowHit.title) || '',
            nowRange: rangeText(start, end) || (nowHit && nowHit.range) || '',
            nowStart: start,
            nowEnd: end,
            nextTitle: next.Name || (item.ProviderIds && (item.ProviderIds.LiveTvNext || item.ProviderIds.livetvnext)) || (nextHit && nextHit.title) || '',
            nextRange: rangeText(next.StartDate, next.EndDate) || (nextHit && nextHit.range) || '',
            percent: progressOf(item, Date.now()),
            folder: isGroupFolder(item),
            folderLine: item.Overview || ''
        };
    }

    function progressOf(item, nowMs) {
        var program = item.CurrentProgram || item;
        var start = Date.parse(program.StartDate || item.StartDate || item.PremiereDate || '');
        var end = Date.parse(program.EndDate || item.EndDate || '');
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
        var client = api();
        if (!client || !item || !item.Id || typeof client.getImageUrl !== 'function') {
            return '';
        }
        if (item.ImageTags && item.ImageTags.Primary) {
            return client.getImageUrl(item.Id, { type: 'Primary', tag: item.ImageTags.Primary, maxHeight: 160 });
        }
        try {
            return client.getImageUrl(item.Id, { type: 'Primary', maxHeight: 160 });
        } catch (e) {
            return '';
        }
    }

    function visibleItems() {
        var q = filter.replace(/\s+/g, ' ').trim().toLowerCase();
        if (!q) {
            return items.slice();
        }
        return items.filter(function (item) {
            var guide = guideOf(item);
            var blob = [guide.channelName, guide.nowTitle, guide.nextTitle, item.Number, item.ChannelNumber]
                .join(' ')
                .toLowerCase();
            return blob.indexOf(q) !== -1;
        });
    }

    function isStalePage(node) {
        if (!node || !node.classList) {
            return true;
        }
        if (node.id === 'loginPage' || node.id === 'startupPage') {
            return true;
        }
        return node.classList.contains('hide') || node.classList.contains('standalonePage');
    }

    function visibleHost() {
        var pages = document.querySelectorAll('.page.libraryPage, .page.liveTvPage, .libraryPage, .liveTvPage');
        var i;
        var page;
        var padded;
        for (i = 0; i < pages.length; i++) {
            page = pages[i];
            if (isStalePage(page)) {
                continue;
            }
            padded = page.querySelector('.padded-right.padded-left, .padded-right');
            return padded || page;
        }
        pages = document.querySelectorAll('.mainAnimatedPage');
        for (i = 0; i < pages.length; i++) {
            page = pages[i];
            if (isStalePage(page)) {
                continue;
            }
            return page;
        }
        return null;
    }

    function overlayOnVisiblePage() {
        var box = document.getElementById('jf-livetv-overview');
        var host = visibleHost();
        return !!(box && host && host.contains(box));
    }

    function applyListMode() {
        document.documentElement.classList.toggle('jf-livetv-list-on', owned && overlayOnVisiblePage());
        if (owned) {
            hideLibrarySpinner();
        }
    }

    function ensure() {
        var host = visibleHost();
        var box = document.getElementById('jf-livetv-overview');
        if (!host) {
            return box;
        }
        if (box && host.contains(box)) {
            return box;
        }
        if (!box) {
            box = document.createElement('div');
            box.id = 'jf-livetv-overview';
            box.className = 'verticalSection';
        }
        if (host.firstChild) {
            host.insertBefore(box, host.firstChild);
        } else {
            host.appendChild(box);
        }
        return box;
    }

    function play(item, list) {
        if (isGroupFolder(item)) {
            location.hash = '#/list?parentId=' + item.Id + '&ltvgroup=1';
            return;
        }
        if (window.FireTvLive && typeof window.FireTvLive.play === 'function') {
            window.FireTvLive.play(item, list || items);
            return;
        }
        if (window.playbackManager && typeof window.playbackManager.play === 'function') {
            window.playbackManager.play({ items: [item], fullscreen: true });
            return;
        }
        if (item && item.Id) {
            location.hash = '#/details?id=' + item.Id;
        }
    }

    function applyGuide(row, item, de) {
        var guide = guideOf(item);
        var show = row.querySelector('.jf-livetv-show');
        var times = row.querySelector('.jf-livetv-times');
        var fill = row.querySelector('.jf-livetv-bar > span');
        var bar = row.querySelector('.jf-livetv-bar');
        var next = row.querySelector('.jf-livetv-next');
        if (guide.folder) {
            if (show) {
                show.textContent = guide.folderLine || (de ? 'Ordner' : 'Folder');
            }
            if (times) {
                times.textContent = '';
            }
            if (next) {
                next.textContent = '';
            }
            return;
        }
        if (show) {
            show.textContent = guide.nowTitle
                ? ((de ? 'Jetzt: ' : 'Now: ') + guide.nowTitle)
                : (de ? 'Keine EPG-Daten' : 'No guide data');
        }
        if (times) {
            times.textContent = guide.nowRange;
        }
        if (guide.percent != null && fill && bar) {
            fill.style.width = guide.percent + '%';
            bar.setAttribute('aria-valuenow', String(Math.round(guide.percent)));
        }
        if (next) {
            next.textContent = guide.nextTitle
                ? ((de ? 'Danach: ' : 'Next: ') + guide.nextTitle + (guide.nextRange ? ' · ' + guide.nextRange : ''))
                : '';
        }
    }

    function buildRow(item, index, de, shown, search) {
        var guide = guideOf(item);
        var row = document.createElement('button');
        row.type = 'button';
        row.className = 'jf-livetv-row';
        row.setAttribute('role', 'listitem');
        row.setAttribute('data-id', item.Id || '');
        row.setAttribute('data-type', item.Type || (guide.folder ? 'Folder' : 'TvChannel'));
        row.tabIndex = 0;
        var logo = document.createElement('div');
        logo.className = 'jf-livetv-logo';
        var url = posterUrl(item);
        if (url) {
            logo.style.backgroundImage = "url('" + String(url).replace(/'/g, '%27') + "')";
        }
        var sender = document.createElement('div');
        sender.className = 'jf-livetv-sender';
        var number = item.Number || item.ChannelNumber || '';
        sender.textContent = number ? number + '  ' + guide.channelName : guide.channelName;
        var now = document.createElement('div');
        now.className = 'jf-livetv-now';
        now.appendChild(document.createElement('div')).className = 'jf-livetv-show';
        now.appendChild(document.createElement('div')).className = 'jf-livetv-times';
        var bar = document.createElement('div');
        bar.className = 'jf-livetv-bar';
        bar.setAttribute('role', 'progressbar');
        bar.setAttribute('aria-valuemin', '0');
        bar.setAttribute('aria-valuemax', '100');
        bar.appendChild(document.createElement('span'));
        now.appendChild(bar);
        var next = document.createElement('div');
        next.className = 'jf-livetv-next';
        row.appendChild(logo);
        row.appendChild(sender);
        row.appendChild(now);
        row.appendChild(next);
        applyGuide(row, item, de);
        row.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            play(item, shown);
        });
        if (index === 0 && document.activeElement !== search) {
            window.setTimeout(function () { row.focus(); }, 40);
        }
        return row;
    }

    function updateProgress(box, shown) {
        var de = german();
        var byId = {};
        shown.forEach(function (item) {
            if (item && item.Id) {
                byId[item.Id] = item;
            }
        });
        var rows = box.querySelectorAll('.jf-livetv-row');
        var i;
        for (i = 0; i < rows.length; i++) {
            var item = byId[rows[i].getAttribute('data-id')];
            if (item) {
                applyGuide(rows[i], item, de);
            }
        }
    }

    function render(mode) {
        var box = ensure();
        if (!box) {
            return;
        }
        var de = german();
        var shown = visibleItems();
        if (mode === 'progress' && box.querySelector('.jf-livetv-list')) {
            updateProgress(box, shown);
            return;
        }
        paintToken += 1;
        var token = paintToken;
        box.innerHTML = '';
        var head = document.createElement('div');
        head.className = 'jf-livetv-head';
        var title = document.createElement('div');
        title.className = 'jf-livetv-title';
        title.textContent = parentItem && parentItem.Name && parentItem.Name !== 'Live TV' ? parentItem.Name : 'Live TV';
        var meta = document.createElement('div');
        meta.className = 'jf-livetv-count';
        meta.textContent = shown.length + (de ? ' Sender' : ' channels');
        var search = document.createElement('input');
        search.type = 'search';
        search.className = 'jf-livetv-search';
        search.placeholder = de ? 'Sender oder Sendung' : 'Channel or show';
        search.value = filter;
        search.setAttribute('aria-label', search.placeholder);
        search.addEventListener('input', function () {
            filter = search.value;
            render();
            var again = document.querySelector('#jf-livetv-overview .jf-livetv-search');
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

        var cols = document.createElement('div');
        cols.className = 'jf-livetv-cols';
        cols.setAttribute('aria-hidden', 'true');
        cols.innerHTML = '<span></span><span>' + (de ? 'Sender' : 'Channel') + '</span><span>' +
            (de ? 'Jetzt' : 'Now') + '</span><span>' + (de ? 'Danach' : 'Next') + '</span>';

        var list = document.createElement('div');
        list.className = 'jf-livetv-list';
        list.setAttribute('role', 'list');
        box.appendChild(head);
        box.appendChild(cols);
        box.appendChild(list);
        if (!shown.length) {
            var empty = document.createElement('div');
            empty.className = 'jf-livetv-empty';
            if (loading) {
                empty.textContent = de ? 'Sender werden geladen…' : 'Loading channels…';
            } else if (loadError) {
                empty.textContent = de
                    ? 'Live TV konnte nicht geladen werden. Tuner und Guide auf dem Server prüfen.'
                    : 'Live TV could not be loaded. Check the tuner and guide on the server.';
            } else {
                empty.textContent = de
                    ? 'Keine Sender. Tuner und Guide auf dem Server prüfen.'
                    : 'No channels. Check the tuner and guide on the server.';
            }
            list.appendChild(empty);
            return;
        }
        var offset = 0;
        function paintChunk() {
            if (token !== paintToken) {
                return;
            }
            var end = Math.min(offset + ROW_CHUNK, shown.length);
            var i;
            for (i = offset; i < end; i++) {
                list.appendChild(buildRow(shown[i], i, de, shown, search));
            }
            offset = end;
            if (offset < shown.length && typeof window.requestAnimationFrame === 'function') {
                window.requestAnimationFrame(paintChunk);
            } else if (offset < shown.length) {
                window.setTimeout(paintChunk, 0);
            }
        }
        paintChunk();
    }

    function show(on) {
        owned = on;
        if (on) {
            ensure();
            hideLibrarySpinner();
        }
        var box = document.getElementById('jf-livetv-overview');
        if (!on && box) {
            box.remove();
        }
        applyListMode();
        var legacy = document.getElementById('firetv-live');
        if (on && legacy) {
            legacy.remove();
            document.documentElement.classList.remove('firetv-live-on');
            if (document.body) {
                document.body.classList.remove('firetv-live-on');
            }
        }
        if (on && !tickTimer) {
            tickTimer = window.setInterval(function () {
                if (owned && items.length) {
                    render('progress');
                }
            }, 15000);
        }
        if (!on && tickTimer) {
            window.clearInterval(tickTimer);
            tickTimer = 0;
        }
    }

    /* Items / LiveTv/Channels on this Jellyfin only — never the IPTV playlist. */
    function fetchOfficial() {
        var client = api();
        if (!client || typeof client.getJSON !== 'function') {
            return Promise.resolve([]);
        }
        var url = client.getUrl('LiveTv/Channels', {
            userId: client.getCurrentUserId(),
            addCurrentProgram: true,
            enableImages: true,
            fields: 'ChannelNumber,Overview,Tags,ProviderIds',
            limit: 800
        });
        return client.getJSON(url).then(function (result) {
            return ((result && result.Items) || []).filter(function (item) { return item && item.Id; });
        });
    }

    function fetchChildren(parentId) {
        var client = api();
        if (!client || typeof client.getItems !== 'function') {
            return Promise.resolve([]);
        }
        return client.getItems(client.getCurrentUserId(), {
            ParentId: parentId,
            Fields: 'OriginalTitle,Overview,ProviderIds,PremiereDate,StartDate,EndDate,CompletionPercentage,Tags,ExternalId',
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Limit: 800
        }).then(function (result) {
            return ((result && result.Items) || []).filter(function (item) { return item && item.Id; });
        });
    }

    function load() {
        var parentId = parentIdFromHash();
        var key = hash() + '|' + parentId + '|' + pageTitle();
        if (loading && lastKey === key) {
            show(true);
            hideLibrarySpinner();
            return;
        }
        if (items.length && lastKey === key) {
            show(true);
            hideLibrarySpinner();
            render();
            return;
        }
        lastKey = key;
        var gen = ++loadGen;
        loading = true;
        loadError = '';
        emptyRetry = 0;
        show(true);
        hideLibrarySpinner();
        render();
        var work = isOfficialLiveHash()
            ? fetchOfficial().then(function (list) { parentItem = { Name: 'Live TV' }; return list; })
            : api().getItem(api().getCurrentUserId(), parentId).then(function (item) {
                parentItem = item;
                return fetchChildren(parentId);
            });
        work.then(function (list) {
            if (gen !== loadGen) {
                return;
            }
            hideLibrarySpinner();
            items = list || [];
            if (!items.length && emptyRetry < EMPTY_RETRY_MS.length) {
                loading = true;
                render();
                scheduleEmptyRetry(gen, parentId, isOfficialLiveHash());
                return;
            }
            loading = false;
            render();
        }).catch(function () {
            if (gen !== loadGen) {
                return;
            }
            loading = false;
            hideLibrarySpinner();
            loadError = 'error';
            items = [];
            render();
        });
    }

    function scheduleEmptyRetry(gen, parentId, official) {
        if (emptyRetry >= EMPTY_RETRY_MS.length) {
            loading = false;
            render();
            return;
        }
        var delay = EMPTY_RETRY_MS[emptyRetry];
        emptyRetry += 1;
        window.setTimeout(function () {
            if (gen !== loadGen) {
                return;
            }
            if (!official && parentId && parentIdFromHash() !== parentId) {
                return;
            }
            var again = official ? fetchOfficial() : fetchChildren(parentId);
            again.then(function (list) {
                if (gen !== loadGen) {
                    return;
                }
                hideLibrarySpinner();
                if (list && list.length) {
                    loading = false;
                    items = list;
                    render();
                    return;
                }
                scheduleEmptyRetry(gen, parentId, official);
            }).catch(function () {
                if (gen !== loadGen) {
                    return;
                }
                scheduleEmptyRetry(gen, parentId, official);
            });
        }, delay);
    }

    function hide() {
        lastKey = '';
        show(false);
    }

    function decide(parent) {
        if (isGuidePage() || looksTreasureMaps(parent)) {
            return false;
        }
        if (isOfficialLiveHash() || isLiveTvGroupHash()) {
            return true;
        }
        if (parent && isLiveTvParent(parent)) {
            return true;
        }
        return false;
    }

    function sync() {
        if (isGuidePage()) {
            hide();
            return;
        }
        var parentId = parentIdFromHash();
        if (isOfficialLiveHash() || isLiveTvGroupHash()) {
            load();
            return;
        }
        if (!parentId || !api()) {
            hide();
            return;
        }
        api().getItem(api().getCurrentUserId(), parentId).then(function (item) {
            if (decide(item)) {
                load();
            } else {
                hide();
            }
        }).catch(function () {
            hide();
        });
    }

    function start() {
        window.addEventListener('hashchange', function () {
            lastKey = '';
            sync();
        });
        var observer = new MutationObserver(function () {
            if (owned) {
                var host = visibleHost();
                var box = document.getElementById('jf-livetv-overview');
                if (host && (!box || !host.contains(box))) {
                    ensure();
                    applyListMode();
                    if (items.length || loading) {
                        render();
                    }
                } else {
                    applyListMode();
                }
                return;
            }
            window.clearTimeout(syncTimer);
            syncTimer = window.setTimeout(sync, 350);
        });
        if (document.documentElement) {
            observer.observe(document.documentElement, { childList: true, subtree: true });
        }
        window.clearInterval(refreshTimer);
        refreshTimer = window.setInterval(function () {
            if (owned) {
                lastKey = '';
                sync();
            }
        }, 120000);
        sync();
    }

    window.JellyfinLiveTvOverview = {
        __bound: true,
        ownsPage: function () { return owned; },
        visibleHost: visibleHost,
        sync: sync,
        progressOf: progressOf,
        guideOf: guideOf,
        isLiveTvItem: isLiveTvItem,
        play: play
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
