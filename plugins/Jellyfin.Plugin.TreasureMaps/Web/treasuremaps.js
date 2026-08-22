/* Treasure-Maps client enhancements (injected into the Jellyfin web client):
   - Movie details pages of Treasure-Maps titles get a proper "Releases" LIST with a
     Download button per release (instead of the generic children card row).
   - Downloads is a title+poster+progress list (not a grid of quality strings),
     filtered to Treasure-Maps grabs, with large tap targets for phones and TV.
   - Downloads show live SABnzbd status (progress %, speed, ETA, completed/failed).
   - Hover play overlays are hidden on Treasure-Maps channel pages (nothing is playable). */
(function () {
    'use strict';

    var pollTimer = null;
    var lastHash = null;

    var style = document.createElement('style');
    style.textContent =
        '.tmChannelPage .cardOverlayFab-primary{display:none!important}' +
        '#tmReleases{margin:1.2em 0;max-width:100%;box-sizing:border-box}' +
        '#tmReleases .tmRelRow{display:flex;flex-wrap:wrap;align-items:flex-start;gap:.55em .75em;' +
        'padding:.65em .8em;margin:.35em 0;border-radius:12px;background:rgba(255,255,255,.07);' +
        'border:1px solid rgba(255,255,255,.1);box-sizing:border-box;max-width:100%}' +
        '#tmReleases .tmRelRow:hover{background:rgba(255,255,255,.12)}' +
        '#tmReleases .tmRelName{flex:1 1 12rem;min-width:0;white-space:normal;overflow:visible;' +
        'overflow-wrap:anywhere;word-break:break-word;line-height:1.35}' +
        '#tmReleases .tmRelMeta{display:flex;flex:0 1 auto;flex-wrap:wrap;align-items:center;gap:.55em;' +
        'margin-left:auto;max-width:100%}' +
        '#tmReleases .tmRelStatus{flex:1 1 auto;font-size:.9em;opacity:.9;min-width:0;text-align:right}' +
        '#tmReleases .tmRelStatus:empty{display:none}' +
        '#tmReleases .tmDl{flex:0 0 auto;border:none;border-radius:999px;padding:.45em 1.1em;cursor:pointer;' +
        'background:#0a84ff;color:#fff;font-weight:600;font-family:inherit;white-space:nowrap}' +
        '#tmReleases .tmDl:disabled{background:rgba(255,255,255,.18);cursor:default}' +
        '@media (max-width:700px){' +
        '#tmReleases .tmRelRow{flex-direction:column;align-items:stretch}' +
        '#tmReleases .tmRelName{flex:1 1 auto}' +
        '#tmReleases .tmRelMeta{margin-left:0;width:100%;justify-content:space-between}' +
        '#tmReleases .tmRelStatus{text-align:left}' +
        '}' +
        '.tmDetailDl{margin-left:.4em}' +
        '#tmDownloads{margin:.4em 0 1.6em;max-width:52rem;box-sizing:border-box}' +
        '#tmDownloads .tmDlRow{display:flex;align-items:stretch;gap:1rem;padding:1rem 1.1rem;' +
        'margin:.65em 0;border-radius:16px;background:rgba(255,255,255,.07);' +
        'border:1px solid rgba(255,255,255,.12);box-sizing:border-box;min-height:7.25rem;' +
        'cursor:pointer;-webkit-tap-highlight-color:transparent}' +
        '#tmDownloads .tmDlRow:hover,#tmDownloads .tmDlRow:focus{background:rgba(255,255,255,.13);outline:none}' +
        '#tmDownloads .tmDlPoster{flex:0 0 5.4rem;width:5.4rem;height:8.1rem;border-radius:10px;' +
        'background:rgba(0,0,0,.35) center/cover no-repeat;overflow:hidden}' +
        '#tmDownloads .tmDlBody{flex:1 1 auto;min-width:0;display:flex;flex-direction:column;justify-content:center;gap:.35em}' +
        '#tmDownloads .tmDlTitle{font-size:1.2em;font-weight:700;line-height:1.25;overflow-wrap:anywhere}' +
        '#tmDownloads .tmDlMeta{font-size:.92em;opacity:.85;overflow-wrap:anywhere}' +
        '#tmDownloads .tmDlBar{height:8px;border-radius:99px;background:rgba(255,255,255,.12);overflow:hidden}' +
        '#tmDownloads .tmDlBar>span{display:block;height:100%;width:0;background:#0a84ff;border-radius:99px}' +
        '#tmDownloads .tmDlOpen{flex:0 0 auto;align-self:center;border:none;border-radius:999px;' +
        'min-height:2.75rem;padding:.55em 1.15em;background:#0a84ff;color:#fff;font-weight:600;' +
        'font-family:inherit;cursor:pointer}' +
        '.tmDownloadsPage .itemsContainer,.tmDownloadsPage .alphaPicker{display:none!important}' +
        '.tmDownloadHero{display:flex;gap:1rem;align-items:center;margin:0 0 1em;flex-wrap:wrap}' +
        '.tmDownloadHero .tmDlBar{flex:1 1 12rem;height:10px;border-radius:99px;background:rgba(255,255,255,.12)}' +
        '.tmDownloadHero .tmDlBar>span{display:block;height:100%;background:#0a84ff;border-radius:99px}' +
        '@media (max-width:700px){' +
        '#tmDownloads .tmDlRow{flex-wrap:wrap}' +
        '#tmDownloads .tmDlOpen{width:100%}' +
        '}';
    document.head.appendChild(style);

    setInterval(function () {
        if (location.hash !== lastHash) {
            lastHash = location.hash;
            stopPoll();
            setTimeout(onNavigate, 350);
        }
    }, 350);

    function api() { return window.ApiClient; }

    function onNavigate() {
        if (!api()) { return; }
        var hash = location.hash || '';
        var details = hash.match(/[#/]details\?id=([a-f0-9]{32})/i);
        var list = hash.match(/[#/]list\?parentId=([a-f0-9]{32})/i);

        if (details) {
            api().getItem(api().getCurrentUserId(), details[1]).then(function (item) {
                if (isDownloadsFolder(item)) {
                    enhanceDownloadsList(item);
                } else if (isDownloadItem(item)) {
                    enhanceDownloadDetail(item);
                } else if (item.Type === 'BoxSet' && item.ChannelId) {
                    enhanceTitlePage(item);
                } else if (item.ProviderIds && item.ProviderIds.TreasureMaps) {
                    enhanceReleasePage(item);
                }
            }).catch(function () { });
        } else if (list) {
            api().getItem(api().getCurrentUserId(), list[1]).then(function (item) {
                if (isDownloadsFolder(item)) {
                    enhanceDownloadsList(item);
                    return;
                }
                if (isDownloadItem(item)) {
                    enhanceDownloadDetail(item);
                    return;
                }
                if (item.Type === 'Channel' || item.ChannelId) {
                    var page = visiblePage();
                    if (page) { page.classList.add('tmChannelPage'); }
                }
            }).catch(function () { });
        }
    }

    function visiblePage() {
        var pages = document.querySelectorAll('.page:not(.hide)');
        return pages.length ? pages[pages.length - 1] : null;
    }

    function whenReady(selector, tries, callback) {
        var page = visiblePage();
        var el = page && page.querySelector(selector);
        if (el) { callback(page, el); return; }
        if (tries > 0) { setTimeout(function () { whenReady(selector, tries - 1, callback); }, 350); }
    }

    function looksQuality(name) {
        return /^(?:\d{3,4}p|4k|uhd|sd)\b/i.test(name || '')
            || /\d{3,4}p\s*[·•]\s*(WEB|BLU|HDTV|CAM|TS)/i.test(name || '')
            || /start download/i.test(name || '');
    }

    function isDownloadsFolder(item) {
        if (!item) { return false; }
        var name = item.Name || '';
        if (!/downloads/i.test(name)) { return false; }
        return !!(item.ChannelId || item.Type === 'Channel');
    }

    function isDownloadItem(item) {
        if (!item || !item.ChannelId) { return false; }
        var ext = String(item.ExternalId || item.Path || '');
        if (/DL::|dl::|dlinfo/i.test(ext)) { return true; }
        var overview = item.Overview || '';
        return item.Type === 'BoxSet' && /Download complete|Download failed|Downloading|SABnzbd|Treasure-Maps download/i.test(overview);
    }

    /* ---- Movie/show title page: replace the generic children row with a release LIST ---- */
    function enhanceTitlePage(item) {
        if (isDownloadItem(item) && !hasReleaseChildrenHint(item)) {
            enhanceDownloadDetail(item);
            return;
        }

        api().getItems(api().getCurrentUserId(), { ParentId: item.Id, Fields: 'ProviderIds' }).then(function (result) {
            var releases = (result.Items || []).filter(function (i) {
                return i.ProviderIds && i.ProviderIds.TreasureMaps;
            });
            if (!releases.length) {
                if (isDownloadItem(item)) { enhanceDownloadDetail(item); }
                return;
            }

            whenReady('.collectionItems', 14, function (page, collection) {
                collection.style.display = 'none';
                var old = page.querySelector('#tmReleases');
                if (old) { old.remove(); }

                var host = document.createElement('div');
                host.id = 'tmReleases';
                host.className = 'verticalSection detailVerticalSection';
                var title = document.createElement('h2');
                title.className = 'sectionTitle';
                title.textContent = 'Releases';
                host.appendChild(title);
                releases.forEach(function (release) { host.appendChild(buildRow(release, item.Name)); });

                collection.parentNode.insertBefore(host, collection);
                refreshStatus();
                startPoll();
            });
        }).catch(function () { });
    }

    function hasReleaseChildrenHint(item) {
        var overview = item.Overview || '';
        return /release/i.test(overview);
    }

    /* ---- Release tile page: add a proper Download button next to the favorite button ---- */
    function enhanceReleasePage(item) {
        applyMovieTitle(item);
        whenReady('.mainDetailButtons', 14, function (page, buttons) {
            if (page.querySelector('.tmDetailDl')) { return; }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'tmDl tmDetailDl';
            btn.textContent = '\u2B07 Download';
            var status = document.createElement('span');
            status.className = 'tmRelStatus';
            status.style.marginLeft = '.8em';
            var movieTitle = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.OriginalTitle || '';
            btn.addEventListener('click', function () {
                grab(item.ProviderIds.TreasureMaps, item.ProviderIds.TreasureMapsKind || 'movie', item.Name, btn, status, null, item.Id, item.ImageTags && item.ImageTags.Primary, movieTitle);
            });
            buttons.appendChild(btn);
            buttons.appendChild(status);
        });
    }

    function applyMovieTitle(item) {
        var movieTitle = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.OriginalTitle;
        if (!movieTitle || movieTitle === item.Name) { return; }
        whenReady('.itemName, h1.name, .nameContainer', 14, function (page, el) {
            var target = page.querySelector('.itemName') || el;
            if (!target || target.dataset.tmTitle === '1') { return; }
            target.dataset.tmTitle = '1';
            var quality = item.Name;
            target.textContent = movieTitle;
            if (quality && looksQuality(quality)) {
                var sub = document.createElement('div');
                sub.className = 'tmDlMeta';
                sub.style.opacity = '.8';
                sub.style.marginTop = '.25em';
                sub.textContent = quality;
                target.parentNode && target.parentNode.insertBefore(sub, target.nextSibling);
            }
        });
    }

    function buildRow(release, movieTitle) {
        var row = document.createElement('div');
        row.className = 'tmRelRow';
        row.dataset.name = release.Name || '';

        var name = document.createElement('div');
        name.className = 'tmRelName';
        name.textContent = release.Name || '';
        name.title = release.Name || '';
        name.style.cursor = 'pointer';
        name.addEventListener('click', function () {
            location.hash = '#/details?id=' + release.Id + '&serverId=' + api().serverId();
        });

        var status = document.createElement('div');
        status.className = 'tmRelStatus';

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'tmDl';
        btn.textContent = '\u2B07 Download';
        var title = (release.ProviderIds && release.ProviderIds.TreasureMapsTitle) || movieTitle || '';
        btn.addEventListener('click', function () {
            grab(release.ProviderIds.TreasureMaps, release.ProviderIds.TreasureMapsKind || 'movie', release.Name, btn, status, row, release.Id, release.ImageTags && release.ImageTags.Primary, title);
        });

        var meta = document.createElement('div');
        meta.className = 'tmRelMeta';
        meta.appendChild(status);
        meta.appendChild(btn);

        row.appendChild(name);
        row.appendChild(meta);
        return row;
    }

    function grab(guid, kind, name, btn, statusEl, row, itemId, imageTag, movieTitle) {
        btn.disabled = true;
        statusEl.textContent = 'Starting\u2026';
        var params = { type: kind, name: name };
        if (movieTitle) { params.title = movieTitle; }
        if (itemId && imageTag) {
            params.poster = api().getUrl('Items/' + itemId + '/Images/Primary', { tag: imageTag });
        }
        api().fetch({
            url: api().getUrl('TreasureMaps/Releases/' + guid + '/Grab', params),
            type: 'POST',
            dataType: 'json'
        }).then(function (res) {
            if (res && res.ok) {
                if (row) { row.dataset.nzo = (res.nzoIds || []).join(','); }
                statusEl.textContent = 'Queued\u2026';
                startPoll();
            } else {
                btn.disabled = false;
                statusEl.textContent = '\u2717 ' + ((res && res.message) || 'failed');
            }
        }, function () {
            btn.disabled = false;
            statusEl.textContent = '\u2717 request failed';
        });
    }

    /* ---- Downloads folder: title list instead of a poster grid of quality strings ---- */
    function enhanceDownloadsList(folder) {
        whenReady('.itemsContainer, .padded-left, .pageTitle', 18, function (page) {
            page.classList.add('tmDownloadsPage', 'tmChannelPage');
            var old = page.querySelector('#tmDownloads');
            if (old) { old.remove(); }

            var host = document.createElement('div');
            host.id = 'tmDownloads';
            host.className = 'verticalSection';
            var heading = document.createElement('h2');
            heading.className = 'sectionTitle';
            heading.textContent = 'Downloads';
            host.appendChild(heading);
            var hint = document.createElement('div');
            hint.className = 'tmDlMeta';
            hint.style.margin = '0 0 .8em';
            hint.textContent = 'Only titles grabbed from Treasure-Maps. Tap a row to open it.';
            host.appendChild(hint);

            var anchor = page.querySelector('.itemsContainer') || page.querySelector('.padded-left') || page;
            if (anchor.parentNode && anchor !== page) {
                anchor.parentNode.insertBefore(host, anchor);
            } else {
                page.appendChild(host);
            }

            Promise.all([
                api().getItems(api().getCurrentUserId(), { ParentId: folder.Id, Fields: 'ProviderIds,Overview,PrimaryImageAspectRatio' }),
                fetchStatus()
            ]).then(function (pair) {
                var children = (pair[0] && pair[0].Items) || [];
                var status = pair[1];
                renderDownloadRows(host, children, status);
                startPoll();
            }).catch(function () { });
        });
    }

    function renderDownloadRows(host, children, status) {
        host.querySelectorAll('.tmDlRow').forEach(function (n) { n.remove(); });
        var items = (status && status.items) || [];
        var rows = [];

        if (items.length) {
            items.forEach(function (entry) {
                var child = matchChild(children, entry);
                rows.push({ entry: entry, child: child });
            });
        } else {
            children.forEach(function (child) {
                if (/no treasure-maps downloads|configure sabnzbd/i.test(child.Name || '')) {
                    return;
                }
                rows.push({ entry: null, child: child });
            });
        }

        if (!rows.length) {
            var empty = document.createElement('div');
            empty.className = 'tmDlMeta';
            empty.textContent = (status && status.message) || 'No Treasure-Maps downloads yet.';
            host.appendChild(empty);
            return;
        }

        rows.forEach(function (row) {
            host.appendChild(buildDownloadRow(row.child, row.entry, status && status.speed));
        });
    }

    function matchChild(children, entry) {
        var nzo = entry.id || '';
        var titleKey = normalize(entry.title || entry.name);
        for (var i = 0; i < children.length; i++) {
            var child = children[i];
            var ext = String(child.ExternalId || '');
            if (nzo && ext.indexOf(nzo) >= 0) { return child; }
            if (titleKey && normalize(child.Name) === titleKey) { return child; }
            if (child.ProviderIds && child.ProviderIds.TreasureMapsTitle && normalize(child.ProviderIds.TreasureMapsTitle) === titleKey) {
                return child;
            }
        }
        return null;
    }

    function buildDownloadRow(child, entry, speed) {
        var title = (entry && entry.title) || (child && child.ProviderIds && child.ProviderIds.TreasureMapsTitle) || (child && child.Name) || 'Download';
        var quality = (entry && entry.quality) || '';
        var row = document.createElement('div');
        row.className = 'tmDlRow';
        row.tabIndex = 0;
        if (entry && entry.id) { row.dataset.nzo = entry.id; }
        row.dataset.name = (entry && entry.name) || (child && child.Name) || '';

        var poster = document.createElement('div');
        poster.className = 'tmDlPoster';
        var cover = entry && entry.cover;
        if (!cover && child && child.ImageTags && child.ImageTags.Primary) {
            cover = api().getUrl('Items/' + child.Id + '/Images/Primary', { tag: child.ImageTags.Primary, maxHeight: 360 });
        }
        if (cover) { poster.style.backgroundImage = 'url("' + cover + '")'; }

        var body = document.createElement('div');
        body.className = 'tmDlBody';
        var name = document.createElement('div');
        name.className = 'tmDlTitle';
        name.textContent = title;
        var meta = document.createElement('div');
        meta.className = 'tmDlMeta';
        meta.textContent = statusText(entry, speed, quality);
        var bar = document.createElement('div');
        bar.className = 'tmDlBar';
        var fill = document.createElement('span');
        fill.style.width = Math.max(0, Math.min(100, Math.round((entry && entry.percent) || (entry && entry.status === 'Completed' ? 100 : 0)))) + '%';
        bar.appendChild(fill);
        body.appendChild(name);
        body.appendChild(meta);
        body.appendChild(bar);

        var open = document.createElement('button');
        open.type = 'button';
        open.className = 'tmDlOpen';
        open.textContent = 'Open';
        function go() {
            if (child && child.Id) {
                location.hash = '#/details?id=' + child.Id + '&serverId=' + api().serverId();
            }
        }
        open.addEventListener('click', function (ev) { ev.stopPropagation(); go(); });
        row.addEventListener('click', go);
        row.addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); go(); }
        });

        row.appendChild(poster);
        row.appendChild(body);
        if (child && child.Id) { row.appendChild(open); }
        return row;
    }

    function statusText(entry, speed, quality) {
        var bits = [];
        if (quality) { bits.push(quality); }
        if (!entry) { return bits.join(' \u00B7 ') || 'Queued'; }
        if (entry.status === 'Completed') { bits.push('\u2713 Downloaded'); }
        else if (entry.status === 'Failed') { bits.push('\u2717 Failed' + (entry.failMessage ? ': ' + entry.failMessage : '')); }
        else {
            bits.push('\u2B07 ' + Math.round(entry.percent || 0) + '%');
            if (speed) { bits.push(speed + 'B/s'); }
            if (entry.timeLeft) { bits.push(entry.timeLeft); }
        }
        return bits.join(' \u00B7 ');
    }

    function enhanceDownloadDetail(item) {
        applyMovieTitle(item);
        var page = visiblePage();
        if (page) { page.classList.add('tmChannelPage'); }
        whenReady('.itemName, .detailImageContainer, .mainDetailButtons', 16, function (pageEl) {
            if (pageEl.querySelector('#tmDownloadHero')) { refreshDownloadHero(); return; }
            var hero = document.createElement('div');
            hero.id = 'tmDownloadHero';
            hero.className = 'tmDownloadHero';
            var meta = document.createElement('div');
            meta.className = 'tmDlMeta';
            meta.id = 'tmDownloadHeroMeta';
            var bar = document.createElement('div');
            bar.className = 'tmDlBar';
            bar.innerHTML = '<span></span>';
            hero.appendChild(meta);
            hero.appendChild(bar);
            var buttons = pageEl.querySelector('.mainDetailButtons');
            var nameEl = pageEl.querySelector('.itemName') || pageEl.querySelector('h1');
            if (buttons && buttons.parentNode) {
                buttons.parentNode.insertBefore(hero, buttons);
            } else if (nameEl && nameEl.parentNode) {
                nameEl.parentNode.appendChild(hero);
            } else {
                pageEl.appendChild(hero);
            }
            refreshDownloadHero();
            startPoll();
        });

        function refreshDownloadHero() {
            fetchStatus().then(function (res) {
                var entry = matchDetailEntry(item, (res && res.items) || []);
                var meta = document.getElementById('tmDownloadHeroMeta');
                var fill = document.querySelector('#tmDownloadHero .tmDlBar>span');
                if (!meta) { return; }
                var title = (item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || (entry && entry.title) || item.OriginalTitle || item.Name;
                applyMovieTitle({ Name: item.Name, OriginalTitle: title, ProviderIds: { TreasureMapsTitle: title } });
                meta.textContent = statusText(entry, res && res.speed, entry && entry.quality) +
                    '  \u2014 After it finishes, the title appears in Movies or TV Shows.';
                if (fill) {
                    fill.style.width = Math.max(0, Math.min(100, Math.round((entry && entry.percent) || (entry && entry.status === 'Completed' ? 100 : 0)))) + '%';
                }
            });
        }
    }

    function matchDetailEntry(item, items) {
        var ext = String(item.ExternalId || '');
        var titleKey = normalize((item.ProviderIds && item.ProviderIds.TreasureMapsTitle) || item.Name);
        for (var i = 0; i < items.length; i++) {
            if (items[i].id && ext.indexOf(items[i].id) >= 0) { return items[i]; }
            if (titleKey && (normalize(items[i].title) === titleKey || normalize(items[i].name) === titleKey)) {
                return items[i];
            }
        }
        return null;
    }

    /* ---- Live SABnzbd status ---- */
    function normalize(value) {
        return (value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
    }

    function fetchStatus() {
        return api().fetch({
            url: api().getUrl('TreasureMaps/Downloads/Status'),
            type: 'GET',
            dataType: 'json'
        }).then(function (res) { return res; }, function () { return { ok: false, items: [] }; });
    }

    function refreshStatus() {
        var rows = document.querySelectorAll('.tmRelRow');
        var dlHost = document.getElementById('tmDownloads');
        if ((!rows.length && !dlHost && !document.getElementById('tmDownloadHero')) || !api()) {
            return Promise.resolve(false);
        }

        return fetchStatus().then(function (res) {
            if (!res || !res.ok) { return false; }
            var anyActive = false;
            rows.forEach(function (row) {
                var entry = matchEntry(row, res.items || []);
                if (!entry) { return; }
                var statusEl = row.querySelector('.tmRelStatus');
                var btn = row.querySelector('.tmDl');
                if (entry.status === 'Completed') {
                    statusEl.textContent = '\u2713 Downloaded';
                    if (btn) { btn.disabled = true; }
                } else if (entry.status === 'Failed') {
                    statusEl.textContent = '\u2717 Failed' + (entry.failMessage ? ': ' + entry.failMessage : '');
                    if (btn) { btn.disabled = false; }
                } else {
                    anyActive = true;
                    var pct = Math.round(entry.percent || 0);
                    var text = '\u2B07 ' + pct + '%';
                    if (res.speed) { text += ' \u00B7 ' + res.speed + 'B/s'; }
                    if (entry.timeLeft) { text += ' \u00B7 ' + entry.timeLeft; }
                    statusEl.textContent = text;
                    if (btn) { btn.disabled = true; }
                }
            });

            if (dlHost) {
                api().getItems(api().getCurrentUserId(), { ParentId: currentParentId(), Fields: 'ProviderIds' }).then(function (result) {
                    renderDownloadRows(dlHost, (result && result.Items) || [], res);
                }).catch(function () {
                    renderDownloadRows(dlHost, [], res);
                });
                anyActive = anyActive || (res.items || []).some(function (i) {
                    return i.status !== 'Completed' && i.status !== 'Failed';
                });
            }

            if (document.getElementById('tmDownloadHero')) {
                anyActive = true;
            }

            return anyActive;
        }, function () { return false; });
    }

    function currentParentId() {
        var list = (location.hash || '').match(/[#/]list\?parentId=([a-f0-9]{32})/i);
        var details = (location.hash || '').match(/[#/]details\?id=([a-f0-9]{32})/i);
        return (list && list[1]) || (details && details[1]) || '';
    }

    function matchEntry(row, items) {
        var nzo = (row.dataset.nzo || '').split(',').filter(Boolean);
        var wanted = normalize(row.dataset.name);
        var byName = null;
        for (var i = 0; i < items.length; i++) {
            var entry = items[i];
            if (nzo.length && entry.id && nzo.indexOf(entry.id) >= 0) { return entry; }
            if (!byName && wanted && (normalize(entry.name) === wanted || normalize(entry.title) === wanted)) {
                byName = entry;
            }
        }
        return byName;
    }

    function startPoll() {
        if (pollTimer) { return; }
        pollTimer = setInterval(function () {
            if (!document.querySelector('.tmRelRow') && !document.getElementById('tmDownloads') && !document.getElementById('tmDownloadHero')) {
                stopPoll();
                return;
            }
            refreshStatus();
        }, 3000);
    }

    function stopPoll() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }
})();
