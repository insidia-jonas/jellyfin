/* Per-playback IPTV selection. Menu reads never open provider connections. */
(function () {
    'use strict';
    if (window.JellyfinIptvSources || window.NativePlayer) { return; }
    var drawer = null, button = null, generation = 0, switching = false, current = null, snapshot = null;
    var scope = '', timer = 0;
    function de() { return /^de/i.test(document.documentElement.lang || navigator.language || ''); }
    function tr(a, b) { return de() ? a : b; }
    function identity() { var a = window.ApiClient; return a && a.getCurrentUserId ? a.serverAddress() + '|' + a.getCurrentUserId() : ''; }
    function element(tag, cls, text) { var n = document.createElement(tag); n.className = cls || ''; if (text != null) { n.textContent = text; } return n; }
    function active() {
        var overview = window.JellyfinLiveTvOverview;
        var built = overview && overview.playerState();
        if (built) { return { kind: 'builtin', item: built.item || { Id: built.itemId, Name: '', Type: 'TvChannel' }, mediaId: built.selectedMediaSourceId || built.mediaSourceId, liveId: built.liveStreamId, handle: built }; }
        var pm = overview && overview.resolvePlaybackManager();
        try {
            var player = pm && pm.getCurrentPlayer();
            var item = player && pm.currentItem(player);
            var source = player && pm.currentMediaSource(player);
            if (item && (item.Type === 'TvChannel' || source && source.IsInfiniteStream)) {
                return { kind: 'manager', item: item, mediaId: source && source.Id, liveId: source && source.LiveStreamId, handle: pm };
            }
        } catch (ignore) { /* no local player */ }
        return null;
    }
    function close() {
        generation++; window.clearTimeout(timer); timer = 0; snapshot = null;
        if (drawer) { drawer.remove(); drawer = null; }
        if (button && button.isConnected) { button.focus(); }
    }
    function line(source) {
        var states = { Reachable: tr('Zuletzt erreichbar', 'Recently playable'), Unstable: tr('Instabil', 'Unstable'), Failed: tr('Mehrfach fehlgeschlagen', 'Repeated failures'), Unknown: tr('Ungeprüft', 'Unknown') };
        var parts = [states[source.Status] || states.Unknown];
        if (source.MedianStartMilliseconds != null) { parts.push(tr('Bild nach ', 'Media after ') + (source.MedianStartMilliseconds / 1000).toLocaleString(de() ? 'de' : 'en', { maximumFractionDigits: 1 }) + ' s'); }
        if (source.LastCheckedUtc) {
            parts.push(new Date(source.LastCheckedUtc).toLocaleString(de() ? 'de-DE' : 'en-GB', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }));
            parts.push(source.ChannelSpecific ? tr('Dieser Sender', 'This channel') : tr('Server-Stichprobe', 'Server sample'));
        }
        return parts.join(' · ');
    }
    function render(message) {
        if (!drawer) { return; }
        var focus = drawer.contains(document.activeElement) && document.activeElement.getAttribute('data-source');
        var body = drawer.querySelector('.jf-iptv-choices'); body.textContent = '';
        var status = drawer.querySelector('.jf-iptv-status');
        status.textContent = message || (snapshot && snapshot.AccountReason ? snapshot.AccountReason === 'ProviderBusy'
            ? tr('Anbieter-Verbindungslimit erreicht. Prüfungen pausieren.', 'Provider connection limit reached. Checks are paused.')
            : tr('Anbieter-Anmeldung prüfen. Sender werden nicht als tot markiert.', 'Check provider credentials. Channels are not marked dead.')
            : tr('Wechsel nur für diese Wiedergabe. Medientests im Leerlauf; kein Ping.', 'Switch this playback only. Idle media tests; these are not ping times.'));
        function row(id, title, detail, selected, state) {
            var n = element('button', 'jf-iptv-choice'); n.type = 'button'; n.dataset.source = id;
            n.setAttribute('aria-pressed', selected ? 'true' : 'false'); n.dataset.state = state || 'Unknown';
            n.appendChild(element('strong', '', (selected ? '✓  ' : '') + title)); n.appendChild(element('small', '', detail));
            n.disabled = switching; n.addEventListener('click', function () { select(id); }); body.appendChild(n);
        }
        if (snapshot && snapshot.AutomaticMediaSourceId) {
            row(snapshot.AutomaticMediaSourceId, tr('Automatisch', 'Automatic'), tr('Standardserver und geprüfte Ersatzquellen', 'Default server and verified alternatives'), !current.mediaId || current.mediaId.indexOf('_iptv_') < 0);
            (snapshot.Sources || []).forEach(function (s) {
                var suffix = s.Id === snapshot.PlayingSourceId ? tr(' · Läuft jetzt', ' · Playing now') : s.IsDefault ? tr(' · Standard', ' · Default') : '';
                row(s.MediaSourceId, s.Name + suffix, line(s), current.mediaId === s.MediaSourceId, s.Status);
            });
        } else if (snapshot && !message) { status.textContent = tr('Keine Ersatzserver für diesen Sender konfiguriert.', 'No alternate servers configured for this channel.'); }
        var retry = element('button', 'jf-iptv-refresh', tr('Messwerte aktualisieren', 'Refresh observations')); retry.type = 'button'; retry.disabled = switching;
        retry.addEventListener('click', function () { load(); }); body.appendChild(retry);
        var restore = focus && Array.prototype.find.call(body.querySelectorAll('[data-source]'), function (n) { return n.dataset.source === focus; });
        if (restore) { restore.focus(); }
    }
    function load() {
        if (!drawer || switching) { return; }
        window.clearTimeout(timer);
        var owner = generation, account = identity(), a = window.ApiClient, timeout;
        var latest = active();
        if (latest && latest.item.Id === current.item.Id) { current = latest; }
        if (!snapshot) { render(tr('Gespeicherte Messwerte werden geladen…', 'Loading saved observations…')); }
        Promise.race([a.getJSON(a.getUrl('LiveTv/Channels/' + encodeURIComponent(current.item.Id) + '/Sources', { liveStreamId: current.liveId || '' })), new Promise(function (_, reject) {
            timeout = window.setTimeout(function () { reject(new Error('timeout')); }, 8000);
        })]).then(function (data) {
            if (owner !== generation || account !== identity() || !drawer) { return; }
            snapshot = data; render();
        }, function () {
            if (owner === generation && drawer) { render(tr('Messwerte konnten nicht geladen werden. Erneut versuchen.', 'Could not load observations. Retry.')); }
        }).then(function () { window.clearTimeout(timeout); if (owner === generation && drawer) { timer = window.setTimeout(load, 30000); } });
    }
    function open() {
        if (switching) { return; }
        var value = active(); if (!value) { return; }
        close(); current = value;
        drawer = element('section', 'jf-iptv-drawer'); drawer.setAttribute('role', 'dialog'); drawer.setAttribute('aria-modal', 'true'); drawer.setAttribute('aria-label', tr('Server wechseln', 'Switch server'));
        var top = element('div', 'jf-iptv-heading'); top.appendChild(element('h2', '', tr('Server wechseln', 'Switch server')));
        var exit = element('button', 'jf-iptv-close', '×'); exit.type = 'button'; exit.setAttribute('aria-label', tr('Schließen', 'Close')); exit.addEventListener('click', close); top.appendChild(exit);
        drawer.appendChild(top); drawer.appendChild(element('p', 'jf-iptv-title', current.item.Name || 'Live TV'));
        drawer.appendChild(element('p', 'jf-iptv-status')); drawer.appendChild(element('div', 'jf-iptv-choices'));
        var fullscreen = document.fullscreenElement;
        (fullscreen && fullscreen.tagName !== 'VIDEO' ? fullscreen : document.body).appendChild(drawer);
        load(); exit.focus();
    }
    // Jellyfin's stop() ends the local player before its stop report finishes.
    // Observe that exact promise instead of sending a second close/stop request.
    function stopManager(value) {
        var a = window.ApiClient, original = a.reportPlaybackStopped, wrapper, timeout;
        return new Promise(function (resolve, reject) {
            if (typeof original !== 'function') { reject(new Error('Missing stop reporting')); return; }
            function finish(error) { window.clearTimeout(timeout); if (a.reportPlaybackStopped === wrapper) { a.reportPlaybackStopped = original; } error ? reject(error) : resolve(); }
            wrapper = function (info) {
                var result = original.apply(this, arguments);
                if (info.ItemId === value.item.Id && (!value.liveId || info.LiveStreamId === value.liveId)) { Promise.resolve(result).then(function () { finish(); }, finish); }
                return result;
            };
            a.reportPlaybackStopped = wrapper;
            timeout = window.setTimeout(function () { finish(new Error('Stop confirmation timeout')); }, 12000);
            try { Promise.resolve(value.handle.stop()).catch(finish); } catch (error) { finish(error); }
        });
    }
    function select(id) {
        if (switching || !snapshot || !drawer) { return; }
        if (id !== snapshot.AutomaticMediaSourceId && !(snapshot.Sources || []).some(function (s) { return s.MediaSourceId === id; })) { return; }
        var value = active();
        if (!value || value.item.Id !== current.item.Id) { close(); return; }
        switching = true; window.clearTimeout(timer);
        var owner = ++generation, account = identity();
        render(tr('Verbindung wird gewechselt…', 'Switching connection…'));
        var operation = value.kind === 'builtin' ? window.JellyfinLiveTvOverview.switchSource(id, function () { return owner === generation && account === identity() && !!drawer; }) : stopManager(value).then(function () {
            if (owner !== generation || account !== identity() || !drawer) { return; }
            var other = active(); if (other && other.item.Id !== value.item.Id) { return; }
            return value.handle.play({ items: [value.item], mediaSourceId: id, startPositionTicks: 0, fullscreen: true });
        });
        Promise.resolve(operation).then(function () { if (owner === generation) { close(); } }, function () {
            if (owner === generation && drawer) { render(tr('Wechsel fehlgeschlagen. Der Anbieter antwortet nicht oder die alte Verbindung ist noch belegt.', 'Switch failed. Provider unavailable or the old connection is still occupied.')); }
        }).then(function () { switching = false; if (drawer) { Array.prototype.forEach.call(drawer.querySelectorAll('button'), function (n) { n.disabled = false; }); } });
    }
    document.addEventListener('keydown', function (e) {
        if (!drawer) { return; }
        if (e.key === 'Escape' || e.key === 'Backspace') { e.preventDefault(); e.stopImmediatePropagation(); close(); return; }
        if (e.key === 'Tab' || e.key === 'ArrowUp' || e.key === 'ArrowDown') {
            var rows = Array.prototype.filter.call(drawer.querySelectorAll('button'), function (n) { return !n.disabled; });
            if (rows.length) { var index = rows.indexOf(document.activeElement); var step = e.key === 'ArrowUp' || e.shiftKey ? -1 : 1; rows[(index + step + rows.length) % rows.length].focus(); }
            e.preventDefault(); e.stopImmediatePropagation();
        }
    }, true);
    function sync() {
        var account = identity(); if (account !== scope) { close(); scope = account; current = null; }
        if (!account || document.hidden) { return; }
        var value = active();
        if (drawer && !switching && (!value || value.item.Id !== current.item.Id)) { close(); }
        var host = value && (value.kind === 'builtin' ? document.querySelector('.jf-livetv-player-bar') : document.querySelector('.videoOsdBottom .buttons') || document.querySelector('.videoOsdBottom'));
        if (!host) { if (button) { button.remove(); button = null; } return; }
        if (button && button.parentNode === host) { return; }
        if (button) { button.remove(); }
        button = element('button', 'jf-iptv-open', tr('Server wechseln', 'Switch server')); button.type = 'button'; button.addEventListener('click', open); host.appendChild(button);
    }
    var style = element('style'); style.textContent =
        '.jf-iptv-open{font:inherit;border:1px solid #65849f;border-radius:12px;padding:.6em 1em;background:#152539;color:#f0f5ff;cursor:pointer;white-space:nowrap}' +
        '.jf-iptv-drawer{position:fixed;right:24px;top:24px;bottom:24px;width:min(440px,calc(100vw - 64px));box-sizing:border-box;z-index:2147483646;padding:24px;background:linear-gradient(145deg,#182639f7,#0b121efc);color:#eef5ff;border:1px solid #506580;border-radius:22px;box-shadow:0 18px 70px #0009;display:flex;flex-direction:column;animation:jf-iptv-in .18s ease-out;font-family:inherit}' +
        '.jf-iptv-heading{display:flex;align-items:center;justify-content:space-between;gap:16px}.jf-iptv-heading h2{margin:0;font-size:1.45rem}.jf-iptv-close{font-size:1.6rem!important}.jf-iptv-title{margin:10px 0;font-weight:600}.jf-iptv-status{font-size:.85rem;color:#b8cbe1;line-height:1.5;margin:0 0 14px}' +
        '.jf-iptv-choices{overflow:auto;min-height:0;display:flex;flex-direction:column;gap:9px;padding:4px}.jf-iptv-drawer button{font:inherit;color:#edf5ff;background:#223349;border:1px solid #4a607d;border-radius:12px;padding:12px 14px;cursor:pointer;text-align:left}.jf-iptv-choice strong{display:block;font-size:1rem}.jf-iptv-choice small{display:block;color:#bed0e5;margin-top:6px;font-size:.78rem;line-height:1.5}' +
        '.jf-iptv-choice[aria-pressed=true]{border-color:#68c4f5;background:#15394c}.jf-iptv-choice[data-state=Reachable]{border-left:3px solid #6acbad}.jf-iptv-choice[data-state=Unstable]{border-left:3px solid #e9bd70}.jf-iptv-choice[data-state=Failed]{border-left:3px solid #e08c94}.jf-iptv-drawer button:focus-visible,.jf-iptv-open:focus-visible{outline:3px solid #d5efff;outline-offset:1px}.jf-iptv-drawer button:disabled{opacity:.5;cursor:wait}' +
        '@keyframes jf-iptv-in{from{transform:translateX(20px);opacity:0}to{transform:none;opacity:1}}@media(prefers-reduced-motion:reduce){.jf-iptv-drawer{animation:none}}';
    document.head.appendChild(style);
    window.JellyfinIptvSources = { open: open, close: close };
    window.setInterval(sync, 1000); sync();
}());
