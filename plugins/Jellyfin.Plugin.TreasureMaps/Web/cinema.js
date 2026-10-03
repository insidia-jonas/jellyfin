/* Cinema UI: native Jellyfin navigation/playback, two bounded library reads, no autoplay. */
(function () {
    'use strict';
    if (window.JellyfinCinema) { return; }
    window.JellyfinCinema = { version: 1 };
    var root = document.documentElement;
    var current = '', owner = '', generation = 0, controller = null, cache = null;
    var started = false, mounted = null, timer = null;
    var classes = ['cinema-ui', 'cinema-home', 'cinema-library', 'cinema-details', 'cinema-browse'];

    function element(tag, cls, value) {
        var node = document.createElement(tag);
        if (cls) { node.className = cls; }
        if (value != null) { node.textContent = value; }
        return node;
    }
    function session() {
        try {
            var api = window.ApiClient;
            var user = api && api.getCurrentUserId();
            var token = api && api.accessToken();
            return user && token ? { api: api, user: user, token: token, key: api.getUrl('System/Info/Public') + '|' + user + '|' + token } : null;
        } catch (_) { return null; }
    }
    function routeKind() {
        var route = (location.hash || '#/home').slice(1).split('?')[0].replace(/\/$/, '');
        if (route === '' || route === '/home') {
            return new URLSearchParams(location.hash.split('?')[1] || '').get('tab') === '1' ? 'browse' : 'home';
        }
        if (/^\/(movies|tv|tvshows)$/.test(route)) { return 'library'; }
        if (route === '/details') { return 'details'; }
        if (/^\/(list|search|livetv|channels|programs|guide|recordings)$/.test(route)) { return 'browse'; }
        return '';
    }
    function release() {
        generation++;
        if (controller) { controller.abort(); controller = null; }
        classes.forEach(function (cls) { root.classList.remove(cls); });
        document.querySelectorAll('[data-cinema-owned]').forEach(function (node) { node.remove(); });
        started = false; mounted = null;
    }
    function valid(stamp, identity) {
        var now = session();
        return generation === stamp && current === location.hash && now && now.key === identity;
    }
    function plain(value) {
        var doc = new DOMParser().parseFromString(String(value || ''), 'text/html');
        return (doc.body.textContent || '').replace(/\s+/g, ' ').trim();
    }
    function title(item) {
        return item.Type === 'Episode' && item.SeriesName ? item.SeriesName : item.Name;
    }
    function detailUrl(item) { return '#/details?id=' + encodeURIComponent(item.Id); }
    function drawHero(host, items, auth, stamp) {
        if (!items.length || !valid(stamp, auth.key) || !host.isConnected) { return; }
        var index = 0;
        var hero = element('section', 'cinema-hero');
        hero.setAttribute('data-cinema-owned', 'hero');
        hero.setAttribute('aria-label', 'Aus deiner Bibliothek');
        var artwork = element('img', 'cinema-hero-art');
        artwork.alt = ''; artwork.decoding = 'async'; artwork.setAttribute('aria-hidden', 'true');
        artwork.addEventListener('error', function () { artwork.hidden = true; });
        hero.appendChild(artwork);
        var body = element('div', 'cinema-hero-body');
        var eyebrow = element('p', 'cinema-eyebrow');
        var name = element('h1', 'cinema-hero-title');
        var meta = element('p', 'cinema-hero-meta');
        var overview = element('p', 'cinema-hero-overview');
        // The registered Jellyfin element delegates itemAction clicks to the native player.
        // Static markup works with both its v0 Web Components polyfill and newer browsers.
        var wrapper = element('div');
        wrapper.innerHTML = '<div is="emby-itemscontainer" class="cinema-hero-actions focuscontainer-x"></div>';
        var actions = wrapper.firstElementChild;
        var play = element('button', 'cinema-button cinema-primary itemAction');
        play.type = 'button';
        var details = element('a', 'cinema-button cinema-secondary', 'Details ansehen');
        var status = element('p', 'cinema-hero-status'); status.setAttribute('role', 'status');
        actions.appendChild(play); actions.appendChild(details);
        [eyebrow, name, meta, overview, actions, status].forEach(function (node) { body.appendChild(node); });
        hero.appendChild(body);
        var navigation = element('div', 'cinema-hero-navigation focuscontainer-x');
        var counter = element('span', 'cinema-hero-counter');
        var prev = element('button', 'cinema-arrow', '←'); prev.type = 'button'; prev.setAttribute('aria-label', 'Vorheriger Titel');
        var next = element('button', 'cinema-arrow', '→'); next.type = 'button'; next.setAttribute('aria-label', 'Nächster Titel');
        navigation.appendChild(counter); navigation.appendChild(prev); navigation.appendChild(next);
        navigation.hidden = items.length < 2;
        hero.appendChild(navigation);

        function show() {
            var item = items[index];
            var resume = Number(item.UserData && item.UserData.PlaybackPositionTicks) > 0;
            eyebrow.textContent = resume ? 'DEIN ABEND GEHT WEITER' : 'AUS DEINER BIBLIOTHEK';
            name.textContent = title(item);
            var info = [item.ProductionYear, item.Type === 'Series' ? 'Serie' : item.Type === 'Episode' ? 'Folge ' + (item.IndexNumber || '') : 'Film'];
            if (item.RunTimeTicks) { info.push(Math.round(item.RunTimeTicks / 600000000) + ' Min.'); }
            if (item.OfficialRating) { info.push(item.OfficialRating); }
            if (item.CommunityRating) { info.push('★ ' + Number(item.CommunityRating).toFixed(1)); }
            meta.textContent = info.filter(Boolean).join('   ·   ');
            overview.textContent = plain(item.Overview) || (item.Genres || []).join(' · ');
            details.href = detailUrl(item);
            play.textContent = resume ? '▶  Weiter ansehen' : '▶  Jetzt ansehen';
            play.hidden = !/^(Movie|Episode)$/.test(item.Type);
            play.dataset.action = resume ? 'resume' : 'play';
            play.dataset.id = item.Id;
            play.dataset.serverid = auth.api.serverId();
            play.dataset.type = item.Type;
            play.dataset.mediatype = 'Video';
            play.dataset.isfolder = 'false';
            play.dataset.positionticks = String(Number(item.UserData && item.UserData.PlaybackPositionTicks) || 0);
            play.disabled = false;
            status.textContent = '';
            counter.textContent = String(index + 1).padStart(2, '0') + ' / ' + String(items.length).padStart(2, '0');
            var imageId = item.Id, tags = item.BackdropImageTags;
            if ((!tags || !tags.length) && item.ParentBackdropItemId) { imageId = item.ParentBackdropItemId; tags = item.ParentBackdropImageTags; }
            if (tags && tags.length) {
                artwork.hidden = false;
                artwork.src = auth.api.getImageUrl(imageId, { type: 'Backdrop', index: 0, tag: tags[0], maxWidth: innerWidth < 700 ? 960 : 1600, quality: 85 });
            } else { artwork.hidden = true; artwork.removeAttribute('src'); }
        }
        prev.addEventListener('click', function () { index = (index + items.length - 1) % items.length; show(); });
        next.addEventListener('click', function () { index = (index + 1) % items.length; show(); });
        play.addEventListener('click', function (event) {
            if (!valid(stamp, auth.key) || typeof actions.notifyRefreshNeeded !== 'function') {
                event.preventDefault(); event.stopPropagation();
                status.textContent = 'Öffne die Details, um die Wiedergabe zu starten.';
            }
        });
        show();
        host.insertBefore(hero, host.firstChild);
    }
    function loadHome(host, auth) {
        if (controller) { controller.abort(); controller = null; }
        started = true; mounted = host;
        var stamp = ++generation;
        if (cache && cache.key === auth.key && cache.expires > Date.now()) { drawHero(host, cache.items, auth, stamp); return; }
        controller = new AbortController();
        var signal = controller.signal;
        var timeout = setTimeout(function () { if (valid(stamp, auth.key) && controller) { controller.abort(); } }, 8000);
        function read(path, params) {
            return fetch(auth.api.getUrl(path, params), { signal: signal, headers: { Authorization: 'MediaBrowser Token="' + auth.token + '"' } })
                .then(function (response) { if (!response.ok) { throw new Error('Library unavailable'); } return response.json(); })
                .then(function (data) { return data.Items || []; }, function () { return []; });
        }
        Promise.all([
            read('UserItems/Resume', { UserId: auth.user, Limit: 3, MediaTypes: 'Video', Fields: 'Overview,Genres' }),
            read('Items', { UserId: auth.user, Recursive: true, IncludeItemTypes: 'Movie,Series', SortBy: 'DateCreated', SortOrder: 'Descending', Limit: 8, Fields: 'Overview,Genres' })
        ]).then(function (results) {
            if (!valid(stamp, auth.key)) { return; }
            var ids = {}, items = results[0].concat(results[1]).filter(function (item) {
                if (!item.Id || ids[item.Id] || item.ChannelId || item.IsVirtualItem || !/^(Movie|Series|Episode)$/.test(item.Type)) { return false; }
                ids[item.Id] = true; return true;
            }).slice(0, 4);
            // Empty/error results never hide the native home or trigger automatic retries.
            if (items.length) { cache = { key: auth.key, expires: Date.now() + 60000, items: items }; }
            drawHero(host, items, auth, stamp);
        }).finally(function () { clearTimeout(timeout); });
    }
    function libraryLead() {
        var page = document.querySelector('#moviesPage:not(.hide),#tvRecommendedPage:not(.hide),#tvshowsPage:not(.hide)');
        if (!page || page.querySelector('.cinema-library-lead')) { return; }
        var lead = element('div', 'cinema-library-lead padded-left padded-right');
        lead.setAttribute('data-cinema-owned', 'library');
        lead.appendChild(element('p', 'cinema-eyebrow', 'DEINE SAMMLUNG'));
        var movies = /^#\/movies(?:\?|$)/.test(location.hash);
        lead.appendChild(element('h1', '', movies ? 'Große Geschichten. Jederzeit.' : 'Noch eine Folge?'));
        var search = element('a', 'cinema-button cinema-secondary', movies ? 'Filme durchsuchen' : 'Serien durchsuchen');
        var parent = new URLSearchParams(location.hash.split('?')[1] || '').get('topParentId');
        search.href = '#/search' + (parent ? '?parentId=' + encodeURIComponent(parent) : '');
        lead.appendChild(search);
        page.insertBefore(lead, page.firstChild);
    }
    function sync() {
        var auth = session(), identity = auth ? auth.key : '', kind = auth ? routeKind() : '';
        if (current !== location.hash || identity !== owner) {
            release(); current = location.hash;
            if (identity !== owner) { cache = null; owner = identity; }
        }
        if (!kind) { if (root.classList.contains('cinema-ui')) { release(); } return; }
        if (!root.classList.contains('cinema-' + kind)) { root.classList.add('cinema-ui', 'cinema-' + kind); }
        if (kind === 'home') {
            var host = document.querySelector('#homeTab.is-active');
            if (host && (!started || mounted !== host || !mounted.isConnected)) { loadHome(host, auth); }
        } else if (kind === 'library') { libraryLead(); }
    }
    function routeChanged() { release(); current = ''; sync(); }
    ['pushState', 'replaceState'].forEach(function (method) {
        var original = history[method];
        history[method] = function () { var result = original.apply(this, arguments); routeChanged(); return result; };
    });
    window.addEventListener('hashchange', sync);
    window.addEventListener('popstate', sync);
    document.addEventListener('viewshow', sync);
    document.addEventListener('pageshow', sync);
    document.addEventListener('visibilitychange', function () {
        if (document.hidden) { clearInterval(timer); timer = null; }
        else { sync(); if (!timer) { timer = setInterval(sync, 1000); } }
    });
    timer = setInterval(sync, 1000);
    sync();
})();
