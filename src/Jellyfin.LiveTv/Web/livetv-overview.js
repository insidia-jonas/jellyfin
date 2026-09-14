/* Live TV overview for jellyfin-web (and the in-repo Fire TV web host).
   Replaces the poster wall with a Kodi-PVR-style list: logo, sender, Jetzt +
   times, progress of the current show, optional Danach. Group folders stay
   folders; opening one uses this same list. Treasure-Maps pages are ignored.

   Clicking a sender always starts playback. It must never fall back to the
   item details page: a Live TV item has no artwork, no cast and no plot, so
   details is a dead end that looks like a gray poster placeholder. When no
   player can be started the list shows an inline error instead. */
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
    var HANDOFF_TIMEOUT_MS = 12000;
    var playGen = 0;
    var playState = null;

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
        'html.jf-livetv-playing #jf-livetv-overview{display:none!important}' +
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
        '#jf-livetv-overview .jf-livetv-error{display:flex;flex-wrap:wrap;align-items:center;gap:.6em 1em;margin:0 0 .85em;' +
        'padding:.8em 1em;border:1px solid rgba(255,89,89,.55);border-radius:12px;background:rgba(255,89,89,.14);line-height:1.35}' +
        '#jf-livetv-overview .jf-livetv-error-text{flex:1 1 18em;min-width:0;overflow-wrap:anywhere}' +
        '#jf-livetv-overview .jf-livetv-error button{padding:.45em 1em;border:0;border-radius:8px;background:#0a84ff;color:#fff;font:inherit;cursor:pointer}' +
        '#jf-livetv-overview .jf-livetv-row[data-busy="1"]{background:rgba(10,132,255,.22);border-color:rgba(10,132,255,.7)}' +
        '#jf-livetv-player{position:fixed;inset:0;z-index:2147483000;background:#000;display:flex;flex-direction:column}' +
        '#jf-livetv-player video{flex:1 1 auto;width:100%;height:100%;background:#000;object-fit:contain}' +
        '#jf-livetv-player .jf-livetv-player-bar{position:absolute;left:0;right:0;top:0;display:flex;align-items:center;' +
        'gap:.9em;padding:.9em 1.2em;background:linear-gradient(rgba(0,0,0,.75),rgba(0,0,0,0));color:#fff}' +
        '#jf-livetv-player .jf-livetv-player-title{font-size:1.25em;font-weight:750}' +
        '#jf-livetv-player .jf-livetv-player-status{opacity:.8;font-size:.95em;flex:1 1 auto;overflow-wrap:anywhere}' +
        '#jf-livetv-player button{padding:.45em .95em;border:0;border-radius:8px;background:rgba(255,255,255,.18);' +
        'color:#fff;font:inherit;cursor:pointer}' +
        '#jf-livetv-player button:focus{outline:2px solid #0a84ff}' +
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

    /* The library page stays mounted underneath a running player, so the sender
       list would paint on top of the picture. */
    function playbackVisible() {
        if (document.getElementById('jf-livetv-player')) {
            return true;
        }
        if (/#\/?video(\?|$)/i.test(hashLower())) {
            return true;
        }
        return !!document.querySelector('.videoPlayerContainer, .videoOsdBottom');
    }

    function applyListMode() {
        var playing = playbackVisible();
        document.documentElement.classList.toggle('jf-livetv-playing', playing);
        document.documentElement.classList.toggle('jf-livetv-list-on', owned && !playing && overlayOnVisiblePage());
        if (owned && !playing) {
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

    /* jellyfin-web exposes playbackManager as a webpack module, and the module id
       differs between builds (source path, hashed id, or a re-export). Try every
       shape rather than a single id: a miss here used to end on the details page. */
    function webpackRequire() {
        var req = null;
        try {
            var chunks = window.webpackChunk || (typeof self !== 'undefined' && self.webpackChunk);
            if (chunks && typeof chunks.push === 'function') {
                chunks.push([['jf-livetv-pm-' + Date.now()], {}, function (r) { req = r; }]);
            }
        } catch (e) { /* not a webpack build */ }
        return typeof req === 'function' ? req : null;
    }

    function unwrapPlaybackManager(mod) {
        if (!mod) {
            return null;
        }
        var candidates = [mod, mod.playbackManager, mod.default, mod.PlaybackManager];
        var i;
        for (i = 0; i < candidates.length; i++) {
            var value = candidates[i];
            if (value && typeof value.play === 'function' && typeof value.stop === 'function') {
                return value;
            }
        }
        return null;
    }

    function resolvePlaybackManager() {
        if (window.playbackManager && typeof window.playbackManager.play === 'function') {
            return window.playbackManager;
        }
        var found = null;
        var req = webpackRequire();
        var ids = [
            './components/playback/playbackmanager.js',
            './src/components/playback/playbackmanager.js',
            'components/playback/playbackmanager',
            './components/playback/playbackmanager'
        ];
        var i;
        if (req) {
            for (i = 0; i < ids.length && !found; i++) {
                try {
                    found = unwrapPlaybackManager(req(ids[i]));
                } catch (ignoreId) { /* try the next shape */ }
            }
            if (!found && req.m) {
                var key;
                for (key in req.m) {
                    if (!Object.prototype.hasOwnProperty.call(req.m, key)
                        || String(key).toLowerCase().indexOf('playbackmanager') === -1) {
                        continue;
                    }
                    try {
                        found = unwrapPlaybackManager(req(key));
                        if (found) {
                            break;
                        }
                    } catch (ignoreKey) { /* try the next module */ }
                }
            }
        }
        if (!found && window.Emby) {
            var importer = window.Emby.importModule || window.Emby['import'];
            if (typeof importer === 'function') {
                for (i = 0; i < ids.length && !found; i++) {
                    try {
                        found = unwrapPlaybackManager(importer.call(window.Emby, ids[i]));
                    } catch (ignoreEmby) { /* try the next shape */ }
                }
            }
        }
        if (found) {
            window.playbackManager = found;
        }
        return found;
    }

    function playableItem(item) {
        var copy = {};
        var key;
        for (key in item) {
            if (Object.prototype.hasOwnProperty.call(item, key)) {
                copy[key] = item[key];
            }
        }
        copy.Type = 'TvChannel';
        copy.MediaType = 'Video';
        copy.IsLiveStream = true;
        copy.IsFolder = false;
        if (!copy.ChannelId) {
            copy.ChannelId = item.Id;
        }
        // An empty MediaSources array makes the player treat the item as
        // unplayable and re-fetch it. Leave it undefined so PlaybackInfo decides.
        if (copy.MediaSources && !copy.MediaSources.length) {
            delete copy.MediaSources;
        }
        var client = api();
        if (!copy.ServerId && client && typeof client.serverId === 'function') {
            copy.ServerId = client.serverId();
        }
        return copy;
    }

    function serverUrl(path) {
        var value = String(path || '');
        if (/^https?:\/\//i.test(value)) {
            return value;
        }
        var client = api();
        var base = client && typeof client.serverAddress === 'function' ? String(client.serverAddress() || '') : '';
        base = base.replace(/\/+$/, '');
        return base + (value.charAt(0) === '/' ? value : '/' + value);
    }

    /* Only transcoding profiles: an IPTV mux is usually mpeg2video + mp2, which no
       browser decodes. Forcing h264/aac in progressive fragmented mp4 keeps this
       player free of hls.js and gives the <video> element frames it can show. */
    function browserPlaybackProfile() {
        return {
            Name: 'Jellyfin Live TV list',
            MaxStreamingBitrate: 20000000,
            MaxStaticBitrate: 20000000,
            MusicStreamingTranscodingBitrate: 384000,
            DirectPlayProfiles: [],
            TranscodingProfiles: [{
                Container: 'mp4',
                Type: 'Video',
                VideoCodec: 'h264',
                AudioCodec: 'aac',
                Protocol: 'http',
                Context: 'Streaming',
                MaxAudioChannels: '2'
            }],
            ContainerProfiles: [],
            CodecProfiles: [{
                Type: 'Video',
                Codec: 'h264',
                Conditions: [
                    { Condition: 'EqualsAny', Property: 'VideoProfile', Value: 'high|main|baseline|constrained baseline', IsRequired: false },
                    { Condition: 'LessThanEqual', Property: 'VideoLevel', Value: '51', IsRequired: false }
                ]
            }],
            SubtitleProfiles: []
        };
    }

    function apiPost(path, query, body) {
        var client = api();
        if (!client || typeof client.getUrl !== 'function') {
            return Promise.reject(new Error('no ApiClient'));
        }
        var url = client.getUrl(path, query || {});
        if (typeof client.ajax === 'function') {
            return client.ajax({
                type: 'POST',
                url: url,
                data: body == null ? null : JSON.stringify(body),
                contentType: 'application/json',
                dataType: body == null ? undefined : 'json'
            });
        }
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: body == null ? null : JSON.stringify(body)
        }).then(function (res) {
            if (!res.ok) {
                throw new Error('POST ' + path + ' ' + res.status);
            }
            return body == null ? null : res.json();
        });
    }

    function requestPlaybackInfo(item) {
        var client = api();
        return apiPost('Items/' + item.Id + '/PlaybackInfo', { userId: client.getCurrentUserId() }, {
            UserId: client.getCurrentUserId(),
            MaxStreamingBitrate: 20000000,
            StartTimeTicks: 0,
            AutoOpenLiveStream: true,
            EnableDirectPlay: false,
            EnableDirectStream: false,
            EnableTranscoding: true,
            DeviceProfile: browserPlaybackProfile()
        });
    }

    /* /videos/{id}/stream has no container extension, so the browser has to guess
       the type from the response. Naming the container keeps Chrome on the mp4
       demuxer for the fragmented stream ffmpeg writes. */
    function progressivePath(url) {
        var value = String(url || '');
        var cut = value.indexOf('?');
        var path = cut < 0 ? value : value.slice(0, cut);
        var query = cut < 0 ? '' : value.slice(cut);
        if (/\/stream$/i.test(path)) {
            path += '.mp4';
        }
        return path + query;
    }

    function buildStreamUrl(item, playbackInfo) {
        var source = ((playbackInfo && playbackInfo.MediaSources) || [])[0];
        if (!source) {
            return null;
        }
        var subProtocol = String(source.TranscodingSubProtocol || 'http').toLowerCase();
        if (source.TranscodingUrl && subProtocol === 'http') {
            return serverUrl(progressivePath(source.TranscodingUrl));
        }
        var client = api();
        var params = {
            MediaSourceId: source.Id,
            PlaySessionId: playbackInfo.PlaySessionId,
            VideoCodec: 'h264',
            AudioCodec: 'aac',
            AudioStreamIndex: -1,
            Static: false,
            TranscodingContainer: 'mp4',
            TranscodingProtocol: 'http'
        };
        if (source.LiveStreamId) {
            params.LiveStreamId = source.LiveStreamId;
        }
        return serverUrl(client.getUrl('Videos/' + item.Id + '/stream.mp4', params));
    }

    function mediaElement() {
        var nodes = document.querySelectorAll('video, audio');
        var i;
        for (i = 0; i < nodes.length; i++) {
            var node = nodes[i];
            if (node.currentSrc || node.src || node.srcObject) {
                return node;
            }
        }
        return null;
    }

    /* Chrome refuses unmuted autoplay without a sticky gesture, and Live TV starts
       a good few seconds after the click. Retry muted rather than leave a frozen
       first frame that looks exactly like the bug being fixed. */
    function nudge(element) {
        if (!element || !element.paused || typeof element.play !== 'function') {
            return;
        }
        var started = element.play();
        if (started && typeof started.catch === 'function') {
            started['catch'](function () {
                element.muted = true;
                var retried = element.play();
                if (retried && typeof retried.catch === 'function') {
                    retried['catch'](function () { /* reported by the watchdog */ });
                }
            });
        }
    }

    function playbackTookOver() {
        var element = mediaElement();
        if (!element) {
            return false;
        }
        nudge(element);
        return element.readyState >= 2 || element.currentTime > 0;
    }

    function waitForPlayback(gen, timeoutMs) {
        return new Promise(function (resolve) {
            var deadline = Date.now() + timeoutMs;
            function poll() {
                if (gen !== playGen) {
                    resolve(false);
                    return;
                }
                if (playbackTookOver()) {
                    resolve(true);
                    return;
                }
                if (Date.now() >= deadline) {
                    resolve(false);
                    return;
                }
                window.setTimeout(poll, 300);
            }
            poll();
        });
    }

    function hasNativeLive() {
        return !!(window.FireTvLive && typeof window.FireTvLive.play === 'function');
    }

    function nativeOwnsPlayback() {
        return !!(window.NativePlayer && typeof window.NativePlayer.loadPlayer === 'function');
    }

    function stopManager() {
        var manager = window.playbackManager;
        if (manager && typeof manager.stop === 'function') {
            try {
                manager.stop();
            } catch (e) { /* already stopped */ }
        }
    }

    function play(item, list) {
        if (isGroupFolder(item)) {
            location.hash = '#/list?parentId=' + item.Id + '&ltvgroup=1';
            return Promise.resolve('folder');
        }
        var queue = (list && list.length ? list : items).slice();
        var gen = ++playGen;
        clearError();
        markBusy(item, true);
        return startPlayback(item, queue, gen).then(function (how) {
            if (gen === playGen) {
                markBusy(item, false);
            }
            return how;
        }, function (error) {
            if (gen === playGen) {
                markBusy(item, false);
                showError(item, error);
            }
            return 'error';
        });
    }

    function startPlayback(item, queue, gen) {
        if (hasNativeLive()) {
            try {
                window.FireTvLive.play(item, queue);
            } catch (e) { /* fall through to the web players */ }
            if (nativeOwnsPlayback()) {
                return Promise.resolve('native');
            }
            return waitForPlayback(gen, HANDOFF_TIMEOUT_MS).then(function (ok) {
                if (ok) {
                    return 'native';
                }
                return gen === playGen ? viaManager(item, queue, gen) : 'stale';
            });
        }
        return viaManager(item, queue, gen);
    }

    function viaManager(item, queue, gen) {
        if (gen !== playGen) {
            return Promise.resolve('stale');
        }
        var manager = resolvePlaybackManager();
        if (!manager) {
            return openBuiltinPlayer(item, queue, gen);
        }
        try {
            manager.play({ items: [playableItem(item)], fullscreen: true });
        } catch (e) {
            return openBuiltinPlayer(item, queue, gen);
        }
        return waitForPlayback(gen, HANDOFF_TIMEOUT_MS).then(function (ok) {
            if (ok) {
                return 'playbackManager';
            }
            if (gen !== playGen) {
                return 'stale';
            }
            stopManager();
            return openBuiltinPlayer(item, queue, gen);
        });
    }

    function closeBuiltinPlayer() {
        var state = playState;
        playState = null;
        if (!state) {
            return;
        }
        if (state.keyHandler) {
            document.removeEventListener('keydown', state.keyHandler, true);
        }
        if (state.box && state.box.parentNode) {
            state.box.parentNode.removeChild(state.box);
        }
        var client = api();
        if (client && typeof client.getUrl === 'function') {
            try {
                client.ajax({
                    type: 'DELETE',
                    url: client.getUrl('Videos/ActiveEncodings', {
                        deviceId: typeof client.deviceId === 'function' ? client.deviceId() : '',
                        playSessionId: state.playSessionId || ''
                    })
                })['catch'](function () { /* the transcode may already be gone */ });
            } catch (e) { /* best effort */ }
        }
        if (state.liveStreamId) {
            apiPost('LiveStreams/Close', { liveStreamId: state.liveStreamId }, null)['catch'](
                function () { /* the server closes idle streams anyway */ });
        }
        if (client && typeof client.reportPlaybackStopped === 'function' && state.playSessionId) {
            try {
                client.reportPlaybackStopped({
                    ItemId: state.itemId,
                    PlaySessionId: state.playSessionId,
                    MediaSourceId: state.mediaSourceId,
                    PositionTicks: 0
                });
            } catch (e) { /* reporting is optional */ }
        }
    }

    function zap(step) {
        var state = playState;
        if (!state || !state.queue || state.queue.length < 2) {
            return;
        }
        var index = -1;
        var i;
        for (i = 0; i < state.queue.length; i++) {
            if (state.queue[i] && state.queue[i].Id === state.itemId) {
                index = i;
                break;
            }
        }
        if (index < 0) {
            return;
        }
        var next = state.queue[(index + step + state.queue.length) % state.queue.length];
        var queue = state.queue;
        closeBuiltinPlayer();
        play(next, queue);
    }

    function buildPlayerShell(item) {
        var box = document.createElement('div');
        box.id = 'jf-livetv-player';
        var video = document.createElement('video');
        video.setAttribute('playsinline', '');
        video.autoplay = true;
        video.controls = false;
        var bar = document.createElement('div');
        bar.className = 'jf-livetv-player-bar';
        var close = document.createElement('button');
        close.type = 'button';
        close.textContent = '✕';
        close.setAttribute('aria-label', german() ? 'Schließen' : 'Close');
        close.addEventListener('click', function () { closeBuiltinPlayer(); });
        var title = document.createElement('div');
        title.className = 'jf-livetv-player-title';
        title.textContent = item.Name || '';
        var status = document.createElement('div');
        status.className = 'jf-livetv-player-status';
        status.textContent = german() ? 'Sender wird geöffnet…' : 'Opening channel…';
        var sound = document.createElement('button');
        sound.type = 'button';
        sound.style.display = 'none';
        sound.textContent = german() ? 'Ton an' : 'Unmute';
        sound.addEventListener('click', function () {
            video.muted = false;
            sound.style.display = 'none';
            nudge(video);
        });
        bar.appendChild(close);
        bar.appendChild(title);
        bar.appendChild(status);
        bar.appendChild(sound);
        box.appendChild(video);
        box.appendChild(bar);
        document.body.appendChild(box);
        return { box: box, video: video, status: status, sound: sound, close: close };
    }

    /* Guaranteed player: PlaybackInfo over REST plus a plain <video>. It needs no
       jellyfin-web internals and no hls.js, so it still works on a web build whose
       module graph this script cannot reach. */
    function openBuiltinPlayer(item, queue, gen) {
        var client = api();
        if (!client || !item || !item.Id) {
            return Promise.reject(new Error(german() ? 'Kein Server verfügbar.' : 'No server connection.'));
        }
        closeBuiltinPlayer();
        var shell = buildPlayerShell(item);
        playState = {
            box: shell.box,
            video: shell.video,
            itemId: item.Id,
            queue: queue,
            keyHandler: null
        };
        var state = playState;
        state.keyHandler = function (event) {
            if (playState !== state) {
                return;
            }
            if (event.key === 'Escape' || event.key === 'Backspace' || event.keyCode === 27 || event.keyCode === 8) {
                event.preventDefault();
                event.stopPropagation();
                closeBuiltinPlayer();
            } else if (event.key === 'ArrowUp' || event.keyCode === 38) {
                event.preventDefault();
                zap(-1);
            } else if (event.key === 'ArrowDown' || event.keyCode === 40) {
                event.preventDefault();
                zap(1);
            }
        };
        document.addEventListener('keydown', state.keyHandler, true);
        shell.close.focus();

        return requestPlaybackInfo(item).then(function (info) {
            if (gen !== playGen || playState !== state) {
                return 'stale';
            }
            var source = ((info && info.MediaSources) || [])[0];
            if (!source) {
                throw new Error(german()
                    ? 'Der Server liefert keine Medienquelle für diesen Sender.'
                    : 'The server returned no media source for this channel.');
            }
            state.playSessionId = info.PlaySessionId;
            state.mediaSourceId = source.Id;
            state.liveStreamId = source.LiveStreamId;
            var url = buildStreamUrl(item, info);
            if (!url) {
                throw new Error(german() ? 'Kein abspielbarer Stream.' : 'No playable stream.');
            }
            state.url = url;
            shell.status.textContent = german() ? 'Stream wird geladen…' : 'Loading stream…';
            shell.video.src = url;
            nudge(shell.video);
            shell.video.addEventListener('playing', function () {
                if (playState !== state) {
                    return;
                }
                shell.status.textContent = '';
                shell.video.controls = true;
                if (shell.video.muted) {
                    shell.sound.style.display = '';
                }
            });
            if (client && typeof client.reportPlaybackStart === 'function') {
                try {
                    client.reportPlaybackStart({
                        ItemId: item.Id,
                        PlaySessionId: info.PlaySessionId,
                        MediaSourceId: source.Id,
                        CanSeek: false,
                        IsPaused: false
                    });
                } catch (e) { /* reporting is optional */ }
            }
            return waitForPlayback(gen, HANDOFF_TIMEOUT_MS + 8000).then(function (ok) {
                if (gen !== playGen || playState !== state) {
                    return 'stale';
                }
                if (ok) {
                    return 'builtin';
                }
                closeBuiltinPlayer();
                throw new Error(german()
                    ? 'Der Stream lieferte keine Bilder. Sender oder Transcoder prüfen.'
                    : 'The stream produced no video. Check the channel or the transcoder.');
            });
        }, function (error) {
            if (playState === state) {
                closeBuiltinPlayer();
            }
            throw error;
        });
    }

    function markBusy(item, busy) {
        var box = document.getElementById('jf-livetv-overview');
        if (!box || !item || !item.Id) {
            return;
        }
        var rows = box.querySelectorAll('.jf-livetv-row');
        var i;
        for (i = 0; i < rows.length; i++) {
            if (rows[i].getAttribute('data-id') === item.Id) {
                if (busy) {
                    rows[i].setAttribute('data-busy', '1');
                } else {
                    rows[i].removeAttribute('data-busy');
                }
            }
        }
    }

    function clearError() {
        var node = document.querySelector('#jf-livetv-overview .jf-livetv-error');
        if (node && node.parentNode) {
            node.parentNode.removeChild(node);
        }
    }

    function showError(item, error) {
        clearError();
        var box = document.getElementById('jf-livetv-overview');
        if (!box) {
            return;
        }
        var reason = (error && error.message) || String(error || '');
        var de = german();
        var banner = document.createElement('div');
        banner.className = 'jf-livetv-error';
        banner.setAttribute('role', 'alert');
        var text = document.createElement('div');
        text.className = 'jf-livetv-error-text';
        text.textContent = (de ? 'Wiedergabe fehlgeschlagen: ' : 'Playback failed: ')
            + (item && item.Name ? item.Name + ' — ' : '')
            + (reason || (de ? 'Unbekannter Fehler.' : 'Unknown error.'));
        var retry = document.createElement('button');
        retry.type = 'button';
        retry.textContent = de ? 'Erneut versuchen' : 'Try again';
        retry.addEventListener('click', function () {
            clearError();
            play(item, items);
        });
        banner.appendChild(text);
        banner.appendChild(retry);
        if (box.firstChild) {
            box.insertBefore(banner, box.firstChild);
        } else {
            box.appendChild(banner);
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
        if (playbackVisible()) {
            applyListMode();
            return;
        }
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
                if (playbackVisible()) {
                    applyListMode();
                    return;
                }
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
        play: play,
        resolvePlaybackManager: resolvePlaybackManager,
        playableItem: playableItem,
        buildStreamUrl: buildStreamUrl,
        openBuiltinPlayer: function (item, list) {
            var queue = (list && list.length ? list : items).slice();
            return openBuiltinPlayer(item, queue, ++playGen);
        },
        closePlayer: closeBuiltinPlayer,
        playerState: function () { return playState; }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
