/* Evolution discovery: local vector identity, bounded metadata warming and explicit detail actions. */
(function () {
    'use strict';
    if (window.EvolutionDiscovery) { return; }
    window.EvolutionDiscovery = { version: 1 };
    var route = '', owner = '', generation = 0, controller, item = null, loading = false, mounted = null;
    var warmed = new Set(), results = new Map(), nextRetry = 0, attempts = 0;
    var icons = {
        movies: '<rect x="12" y="18" width="40" height="32" rx="6"/><path d="M12 28h40M22 18l-4 10m17-10-4 10m17-10-4 10"/><path d="m28 34 10 6-10 6z"/>',
        tv: '<rect x="10" y="17" width="44" height="30" rx="7"/><path d="M24 54h16M32 47v7M20 10h24"/><path d="m28 26 11 6-11 6z"/>',
        indexer: '<path d="m32 9 23 13-23 13L9 22zM9 32l23 13 23-13M9 42l23 13 23-13"/><circle cx="32" cy="22" r="3"/>',
        live: '<circle cx="32" cy="32" r="5"/><path d="M22 22a14 14 0 0 0 0 20m20-20a14 14 0 0 1 0 20M15 15a24 24 0 0 0 0 34m34-34a24 24 0 0 1 0 34"/>'
    };
    function node(tag, cls, text) { var n = document.createElement(tag); n.className = cls || ''; if (text != null) { n.textContent = text; } return n; }
    function auth() {
        try { var api = window.ApiClient, user = api && api.getCurrentUserId(), token = api && api.accessToken();
            return user && token ? { api: api, token: token, key: api.serverAddress() + '|' + user + '|' + token } : null;
        } catch (_) { return null; }
    }
    function active(stamp, key) { var a = auth(); return generation === stamp && route === location.hash && a && a.key === key; }
    function clear() {
        generation++; if (controller) { controller.abort(); controller = null; }
        document.querySelectorAll('[data-discovery-owned]').forEach(function (n) { n.remove(); });
        document.querySelectorAll('.evolution-enriched').forEach(function (n) { n.classList.remove('evolution-enriched'); });
        item = null; loading = false; mounted = null; attempts = 0; nextRetry = 0; warmed.clear();
    }
    function artwork() {
        if (!/^#\/home(?:\?|$)/.test(location.hash)) { return; }
        document.querySelectorAll('#homeTab .section0 .card').forEach(function (card) {
            var caption = card.querySelector('.cardText-first');
            var name = (card.getAttribute('aria-label') || (caption && caption.textContent) || card.textContent || '').trim();
            var kind = card.dataset.collectiontype === 'movies' ? 'movies' : card.dataset.collectiontype === 'tvshows' ? 'tv'
                : /^(Indexer|Treasure.?Maps)$/i.test(name) ? 'indexer' : /^Live\s*TV$/i.test(name) ? 'live' : '';
            var host = card.querySelector('.cardScalable');
            if (!kind || !host || host.querySelector('.evolution-hub-art')) { return; }
            card.classList.add('evolution-hub'); card.dataset.hub = kind;
            var art = node('div', 'evolution-hub-art'); art.dataset.discoveryOwned = 'art'; art.setAttribute('aria-hidden', 'true');
            // All SVG markup is a fixed first-party asset; metadata is never interpreted as markup.
            art.innerHTML = '<svg viewBox="0 0 64 64" fill="none" stroke="currentColor" stroke-width="2.3" stroke-linecap="round" stroke-linejoin="round">' + icons[kind] + '</svg>';
            var label = { movies: 'Filme', tv: 'Serien', indexer: 'Indexer', live: 'Live TV' }[kind];
            art.appendChild(node('span', 'evolution-hub-label', label)); host.appendChild(art);
            var text = card.querySelector('.cardText-first bdi'); if (text) { text.textContent = label; }
            card.setAttribute('aria-label', label);
        });
    }
    function request(a, path, options) {
        options = options || {}; options.headers = { Authorization: 'MediaBrowser Token="' + a.token + '"', 'Content-Type': 'application/json' };
        return fetch(a.api.getUrl(path), options);
    }
    function warmVisible(a) {
        if (!document.querySelector('.tmChannelPage:not(.tmCategoryPage):not(.hide)') || document.hidden) { return; }
        var ids = [];
        document.querySelectorAll('.tmChannelPage:not(.hide) .card[data-type="BoxSet"][data-id]').forEach(function (card) {
            var r = card.getBoundingClientRect(), id = card.dataset.id;
            if (ids.length < 24 && warmed.size < 96 && /^[a-f\d]{32}$/i.test(id) && !warmed.has(id) && r.width > 0 && r.bottom > 0 && r.top < innerHeight * 1.8) {
                ids.push(id); warmed.add(id);
            }
        });
        if (!ids.length) { return; }
        var ac = new AbortController(), timeout = setTimeout(function () { ac.abort(); }, 4000);
        request(a, 'TreasureMaps/Metadata/Warm', { method: 'POST', body: JSON.stringify(ids), signal: ac.signal }).catch(function () {}).finally(function () { clearTimeout(timeout); });
    }
    function jump(selector) {
        var target = document.querySelector(selector); if (!target) { return; }
        if (target.tagName === 'DETAILS') { target.open = true; target = target.querySelector('summary') || target; }
        var control = target.matches('button,a,summary,select') ? target : target.querySelector('summary,button:not(:disabled),a[href],select');
        if (control) { control.focus({ preventScroll: true }); target.scrollIntoView({ block: 'center', behavior: 'auto' }); }
    }
    function actions(page) {
        var host = page.querySelector('.detailPagePrimaryContent') || page.querySelector('.detailSection');
        if (!host) { return; }
        var bar = page.querySelector('.evolution-detail-actions');
        if (!bar) {
            bar = node('nav', 'evolution-detail-actions focuscontainer-x'); bar.dataset.discoveryOwned = 'actions'; bar.setAttribute('aria-label', 'Titelaktionen');
            [['Fassungen & Downloads', '#tmReleases'], ['Automatisch anfordern', '#tmArrRequest'], ['Untertitel', '#tmSubtitles']].forEach(function (entry) {
                var button = node('button', '', entry[0]); button.type = 'button'; button.dataset.target = entry[1];
                button.addEventListener('click', function () { jump(entry[1]); }); bar.appendChild(button);
            });
            host.insertBefore(bar, host.firstChild);
        }
        Array.prototype.forEach.call(bar.children, function (b) {
            var target = page.querySelector(b.dataset.target);
            b.hidden = !target || target.hidden || getComputedStyle(target).display === 'none';
            if (b.dataset.target === '#tmArrRequest' && item && !(item.ProviderIds || {}).TreasureMapsKind) { b.hidden = true; }
            if (b.dataset.target === '#tmArrRequest' && item) { b.textContent = (item.ProviderIds || {}).TreasureMapsKind === 'tv' ? 'Serie anfordern · Sonarr' : 'Film anfordern · Radarr'; }
        });
        bar.hidden = !Array.prototype.some.call(bar.children, function (b) { return !b.hidden; });
    }
    function paintMetadata(page, data) {
        var panel = page.querySelector('.evolution-metadata'); if (!panel) { return; }
        panel.replaceChildren(); panel.setAttribute('aria-busy', 'false');
        var meta = node('div', 'evolution-metadata-facts');
        if (data.rating) { meta.appendChild(node('span', 'evolution-rating', (data.ratingSource || 'Bewertung') + '  ★ ' + Number(data.rating).toFixed(1))); }
        if (data.year) { meta.appendChild(node('span', '', data.year)); }
        if (data.genres && data.genres.length) { meta.appendChild(node('span', '', data.genres.slice(0, 3).join(' · '))); }
        if (meta.childNodes.length) { panel.appendChild(meta); }
        var text = String(data.overview || '').trim();
        if (text) {
            // Display a concise synopsis with an explicit action for the full text.
            var plot = node('p', 'evolution-synopsis', text); panel.appendChild(plot);
            if (text.length > 420) {
                plot.classList.add('is-clamped');
                var more = node('button', 'evolution-read-more', 'Mehr lesen'); more.type = 'button'; more.setAttribute('aria-expanded', 'false');
                more.addEventListener('click', function () { var open = plot.classList.toggle('is-clamped'); more.textContent = open ? 'Mehr lesen' : 'Weniger'; more.setAttribute('aria-expanded', String(!open)); }); panel.appendChild(more);
            }
            page.classList.add('evolution-enriched');
        } else { panel.appendChild(node('p', 'evolution-metadata-note', 'Für diesen Titel ist noch keine deutsche Beschreibung verfügbar.')); }
    }
    function detail(a) {
        var match = /^#\/details\?/.test(location.hash) && new URLSearchParams(location.hash.split('?')[1]).get('id');
        var page = document.querySelector('#itemDetailPage:not(.hide)');
        if (!match || !page) { return; }
        if (item) { actions(page); }
        if (loading || mounted === page || Date.now() < nextRetry || attempts >= 3) { return; }
        var host = page.querySelector('.detailPagePrimaryContent'); if (!host) { return; }
        loading = true; attempts++;
        var stamp = generation, key = a.key;
        controller = new AbortController(); var signal = controller.signal;
        var timeout = setTimeout(function () { if (active(stamp, key) && controller) { controller.abort(); } }, 10000);
        Promise.resolve(item || a.api.getItem(a.api.getCurrentUserId(), match)).then(function (value) {
            if (!active(stamp, key) || !page.isConnected) { return; }
            item = value;
            if (!value.ProviderIds || !value.ProviderIds.TreasureMapsKind) { mounted = page; actions(page); return; }
            var panel = page.querySelector('.evolution-metadata');
            if (!panel) { panel = node('section', 'evolution-metadata', 'Filminformationen werden geladen …'); panel.dataset.discoveryOwned = 'metadata'; panel.setAttribute('aria-busy', 'true'); host.insertBefore(panel, host.firstChild); }
            actions(page);
            var cached = results.get(key + '|' + match);
            if (cached && cached.expires > Date.now()) { paintMetadata(page, cached.data); mounted = page; return; }
            return request(a, 'TreasureMaps/Metadata/' + encodeURIComponent(match), { signal: signal }).then(function (r) {
                if (r.status === 202) { throw new Error('pending'); } if (!r.ok) { throw new Error('metadata'); } return r.json();
            }).then(function (data) {
                if (!active(stamp, key) || !page.isConnected) { return; }
                if (data.available) {
                    results.set(key + '|' + match, { data: data, expires: Date.now() + 3600000 });
                    if (results.size > 64) { results.delete(results.keys().next().value); }
                }
                paintMetadata(page, data); mounted = page;
            });
        }).catch(function () {
            if (active(stamp, key)) {
                nextRetry = Date.now() + 4000;
                var panel = page.querySelector('.evolution-metadata'); if (panel) { panel.textContent = attempts < 3 ? 'Filminformationen werden nachgeladen …' : 'Filminformationen sind gerade nicht erreichbar.'; panel.setAttribute('aria-busy', String(attempts < 3)); }
            }
        }).finally(function () { clearTimeout(timeout); if (active(stamp, key)) { loading = false; } });
    }
    function tick() {
        var a = auth();
        if (route !== location.hash || owner !== (a && a.key || '')) { clear(); route = location.hash; if (owner !== (a && a.key || '')) { results.clear(); } owner = a && a.key || ''; }
        if (!a || document.hidden) { return; }
        artwork(); detail(a); warmVisible(a);
    }
    ['pushState', 'replaceState'].forEach(function (name) { var fn = history[name]; history[name] = function () { var result = fn.apply(this, arguments); tick(); return result; }; });
    window.addEventListener('hashchange', tick); window.addEventListener('popstate', tick);
    document.addEventListener('viewshow', tick); document.addEventListener('visibilitychange', tick);
    setInterval(tick, 1200); tick();
})();
