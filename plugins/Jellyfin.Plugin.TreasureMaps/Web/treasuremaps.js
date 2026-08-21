/* Treasure-Maps client enhancements (injected into the Jellyfin web client):
   - Movie details pages of Treasure-Maps titles get a proper "Releases" LIST with a
     Download button per release (instead of the generic children card row).
   - Downloads show live SABnzbd status (progress %, speed, ETA, completed/failed).
   - Hover play overlays are hidden on Treasure-Maps channel pages (nothing is playable). */
(function () {
    'use strict';

    var pollTimer = null;
    var lastHash = null;

    var style = document.createElement('style');
    style.textContent =
        '.tmChannelPage .cardOverlayFab-primary{display:none!important}' +
        '#tmReleases{margin:1.2em 0}' +
        '#tmReleases .tmRelRow{display:flex;align-items:center;gap:.9em;padding:.55em .9em;margin:.3em 0;' +
        'border-radius:12px;background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.1)}' +
        '#tmReleases .tmRelRow:hover{background:rgba(255,255,255,.12)}' +
        '#tmReleases .tmRelName{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
        '#tmReleases .tmRelStatus{flex:0 0 auto;font-size:.9em;opacity:.9;min-width:11em;text-align:right}' +
        '#tmReleases .tmDl{flex:0 0 auto;border:none;border-radius:999px;padding:.45em 1.1em;cursor:pointer;' +
        'background:#0a84ff;color:#fff;font-weight:600;font-family:inherit}' +
        '#tmReleases .tmDl:disabled{background:rgba(255,255,255,.18);cursor:default}' +
        '.tmDetailDl{margin-left:.4em}';
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
                if (item.Type === 'BoxSet' && item.ChannelId) {
                    enhanceTitlePage(item);
                } else if (item.ProviderIds && item.ProviderIds.TreasureMaps) {
                    enhanceReleasePage(item);
                }
            }).catch(function () { });
        } else if (list) {
            api().getItem(api().getCurrentUserId(), list[1]).then(function (item) {
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

    /* ---- Movie/show title page: replace the generic children row with a release LIST ---- */
    function enhanceTitlePage(item) {
        api().getItems(api().getCurrentUserId(), { ParentId: item.Id, Fields: 'ProviderIds' }).then(function (result) {
            var releases = (result.Items || []).filter(function (i) {
                return i.ProviderIds && i.ProviderIds.TreasureMaps;
            });
            if (!releases.length) { return; }

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
                releases.forEach(function (release) { host.appendChild(buildRow(release)); });

                collection.parentNode.insertBefore(host, collection);
                refreshStatus();
                startPoll();
            });
        }).catch(function () { });
    }

    /* ---- Release tile page: add a proper Download button next to the favorite button ---- */
    function enhanceReleasePage(item) {
        whenReady('.mainDetailButtons', 14, function (page, buttons) {
            if (page.querySelector('.tmDetailDl')) { return; }
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'tmDl tmDetailDl';
            btn.textContent = '\u2B07 Download';
            var status = document.createElement('span');
            status.className = 'tmRelStatus';
            status.style.marginLeft = '.8em';
            btn.addEventListener('click', function () {
                grab(item.ProviderIds.TreasureMaps, item.ProviderIds.TreasureMapsKind || 'movie', item.Name, btn, status, null, item.Id, item.ImageTags && item.ImageTags.Primary);
            });
            buttons.appendChild(btn);
            buttons.appendChild(status);
        });
    }

    function buildRow(release) {
        var row = document.createElement('div');
        row.className = 'tmRelRow';
        row.dataset.name = release.Name || '';

        var name = document.createElement('div');
        name.className = 'tmRelName';
        name.textContent = release.Name || '';
        name.title = release.Name || '';
        name.style.cursor = 'pointer';
        // Clicking the name opens the release itself (with its native "Start download" entry),
        // mirroring the tile navigation that TV clients use.
        name.addEventListener('click', function () {
            location.hash = '#/list?parentId=' + release.Id + '&serverId=' + api().serverId();
        });

        var status = document.createElement('div');
        status.className = 'tmRelStatus';

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'tmDl';
        btn.textContent = '\u2B07 Download';
        btn.addEventListener('click', function () {
            grab(release.ProviderIds.TreasureMaps, release.ProviderIds.TreasureMapsKind || 'movie', release.Name, btn, status, row, release.Id, release.ImageTags && release.ImageTags.Primary);
        });

        row.appendChild(name);
        row.appendChild(status);
        row.appendChild(btn);
        return row;
    }

    function grab(guid, kind, name, btn, statusEl, row, itemId, imageTag) {
        btn.disabled = true;
        statusEl.textContent = 'Starting\u2026';
        var params = { type: kind, name: name };
        if (itemId && imageTag) {
            // The item's poster URL, so the Downloads folder tile shows the cover.
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

    /* ---- Live SABnzbd status ---- */
    function normalize(value) {
        return (value || '').toLowerCase().replace(/[^a-z0-9]+/g, '');
    }

    function refreshStatus() {
        var rows = document.querySelectorAll('.tmRelRow');
        if (!rows.length || !api()) { return Promise.resolve(false); }

        return api().fetch({
            url: api().getUrl('TreasureMaps/Downloads/Status'),
            type: 'GET',
            dataType: 'json'
        }).then(function (res) {
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
            return anyActive;
        }, function () { return false; });
    }

    function matchEntry(row, items) {
        var nzo = (row.dataset.nzo || '').split(',').filter(Boolean);
        var wanted = normalize(row.dataset.name);
        var byName = null;
        for (var i = 0; i < items.length; i++) {
            var entry = items[i];
            if (nzo.length && entry.id && nzo.indexOf(entry.id) >= 0) { return entry; }
            if (!byName && wanted && normalize(entry.name) === wanted) { byName = entry; }
        }
        return byName;
    }

    function startPoll() {
        if (pollTimer) { return; }
        pollTimer = setInterval(function () {
            if (!document.querySelector('.tmRelRow')) { stopPoll(); return; }
            refreshStatus();
        }, 3000);
    }

    function stopPoll() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }
})();
