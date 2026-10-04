/* Shared by the browser list and Fire TV. Reading status never starts a stream. */
(function () {
    'use strict';
    if (window.JellyfinChannelHealth) { return; }
    var cache = {};
    var scope = '';
    var generation = 0;
    var pending = false;
    var timer = 0;
    var retryAfter = 0;
    var selector = '.jf-livetv-row[data-id], .firetv-live-row[data-id]';
    var style = document.createElement('style');
    style.textContent = '.jf-channel-health{display:block;font-size:.75em;font-weight:400;line-height:1.4;color:#b7bdc8;white-space:normal}.jf-channel-health[data-state="Healthy"]{color:#77dfa4}.jf-channel-health[data-state="Unstable"]{color:#ffd17b}.jf-channel-health[data-state="Unavailable"]{color:#ff9c9c}.jf-channel-health::before{content:"●";margin-right:.4em}.firetv-live-name .jf-channel-health{font-size:16px}';
    document.head.appendChild(style);

    function german() { return /^de/i.test(document.documentElement.lang || navigator.language || ''); }
    function key() {
        var api = window.ApiClient;
        return api && api.serverAddress && api.getCurrentUserId
            ? api.serverAddress() + '|' + api.getCurrentUserId() + '|' + location.hash : '';
    }
    function describe(health) {
        var de = german();
        var state = health.Status || 'Unknown';
        if (health.LastCheckedUtc && Date.now() - Date.parse(health.LastCheckedUtc) > 30 * 60000) { state = 'Unknown'; }
        var names = de ? { Healthy: 'Zuletzt erreichbar', Unstable: 'Instabil', Unavailable: 'Mehrfach ausgefallen', Unknown: 'Ungeprüft' }
            : { Healthy: 'Recently available', Unstable: 'Unstable', Unavailable: 'Repeated failures', Unknown: 'Unchecked' };
        var text = names[state] || names.Unknown;
        if (state === 'Unstable' && health.Reason === 'ClientPlaybackFailed') {
            text = de ? 'Wiedergabe fehlgeschlagen' : 'Playback failed';
        }
        if (state === 'Unstable' && health.Reason === 'SlowStart') {
            text = de ? 'Langsamer Start' : 'Slow start';
        }
        if (/^Provider/.test(health.Reason || '')) {
            state = 'Unknown';
            text = health.Reason === 'ProviderAuthentication' ? (de ? 'Anbieter-Anmeldung prüfen' : 'Check provider login')
                : health.Reason === 'ProviderBusy' ? (de ? 'Anbieter ausgelastet' : 'Provider busy')
                : (de ? 'Anbieter nicht erreichbar' : 'Provider unavailable');
        } else if (state === 'Unknown' && health.LastCheckedUtc) {
            text = de ? 'Prüfung veraltet' : 'Check expired';
        }
        if (health.LastCheckedUtc) {
            var checked = new Date(health.LastCheckedUtc);
            if (!isNaN(checked.getTime())) {
                text += ' · ' + (de ? 'geprüft ' : 'checked ') + checked.toLocaleTimeString(de ? 'de-DE' : 'en-GB', { hour: '2-digit', minute: '2-digit' });
            }
        }
        return { state: state, text: text };
    }
    function draw(row) {
        var target = row.querySelector('.jf-livetv-sender, .firetv-live-name');
        if (!target) { return; }
        var badge = target.querySelector('.jf-channel-health');
        if (!badge) { badge = document.createElement('span'); badge.className = 'jf-channel-health'; target.appendChild(badge); }
        var health = (cache[row.getAttribute('data-id')] || {}).health || {};
        var value = describe(health);
        if (badge.textContent !== value.text) { badge.textContent = value.text; }
        if (badge.getAttribute('data-state') !== value.state) { badge.setAttribute('data-state', value.state); }
        var details = [];
        if (health.StartMilliseconds != null) {
            details.push((health.StartupMeasurement === 'DecodedMedia' ? (german() ? 'Decodiertes Bild/Ton: ' : 'Decoded media: ')
                : (german() ? 'Erste Mediendaten: ' : 'First media data: ')) + (health.StartMilliseconds / 1000).toFixed(1) + ' s');
        }
        if (health.Interruptions) { details.push(health.Interruptions + (german() ? ' Unterbrechungen' : ' interruptions')); }
        badge.title = details.join(' · ');
    }
    function refresh() {
        timer = 0;
        var current = key();
        if (current !== scope) { scope = current; cache = {}; generation++; pending = false; retryAfter = 0; }
        if (!current || document.hidden) { return; }
        var api = window.ApiClient;
        var rows = Array.prototype.filter.call(document.querySelectorAll(selector), function (row) {
            return !/Folder|BoxSet|Collection/.test(row.getAttribute('data-type') || '') && row.isConnected;
        });
        var ids = [];
        rows.forEach(function (row) {
            draw(row);
            var rect = row.getBoundingClientRect();
            var id = row.getAttribute('data-id');
            if (rect.bottom >= 0 && rect.top <= window.innerHeight && /^[a-f0-9-]{32,36}$/i.test(id)
                && (!cache[id] || cache[id].expires <= Date.now()) && ids.indexOf(id) < 0) { ids.push(id); }
        });
        if (!ids.length || pending || Date.now() < retryAfter || !api.getJSON || !api.getUrl) { return; }
        ids = ids.slice(0, 100);
        var token = generation;
        pending = true;
        var timeout;
        var deadline = new Promise(function (resolve, reject) { timeout = window.setTimeout(function () { reject(new Error('Health request timed out')); }, 10000); });
        Promise.race([api.getJSON(api.getUrl('LiveTv/ChannelHealth', { ids: ids.join(',') })), deadline]).then(function (states) {
            if (token !== generation || current !== key()) { return; }
            ids.forEach(function (id) { cache[id] = { health: states[id] || {}, expires: Date.now() + 15000 }; });
            rows.forEach(function (row) { if (row.isConnected) { draw(row); } });
        }).catch(function () {
            if (token === generation) { retryAfter = Date.now() + 30000; }
        }).then(function () { window.clearTimeout(timeout); if (token === generation) { pending = false; } });
    }
    function schedule() { if (!timer) { timer = window.setTimeout(refresh, 100); } }
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('hashchange', schedule);
    window.addEventListener('popstate', schedule);
    document.addEventListener('scroll', schedule, true);
    document.addEventListener('visibilitychange', schedule);
    window.setInterval(schedule, 15000);
    window.JellyfinChannelHealth = { refresh: schedule };
    schedule();
}());
