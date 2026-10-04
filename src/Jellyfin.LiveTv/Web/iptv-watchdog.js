/* Administrator view of server observations. Opening it never opens IPTV streams. */
(function () {
    'use strict';
    if (window.JellyfinIptvWatchdog) { return; }
    window.JellyfinIptvWatchdog = true;
    var scope = '';
    var generation = 0;
    var pending = false;
    var nextRefresh = 0;
    var snapshot = null;
    var admin = null;
    var scheduled = 0;
    var style = document.createElement('style');
    style.textContent = '.jf-watchdog{margin:0 0 1.2rem;border:1px solid #344055;border-radius:16px;background:linear-gradient(125deg,#182233,#101823);color:#edf2fa;overflow:hidden}' +
        '.jf-watchdog summary{padding:1rem 1.25rem;cursor:pointer;font-weight:700;letter-spacing:.02em}.jf-watchdog summary:focus-visible,.jf-watchdog button:focus-visible{outline:3px solid #63c9ff;outline-offset:-3px}' +
        '.jf-watchdog-body{padding:0 1.25rem 1.25rem}.jf-watchdog p{line-height:1.5;margin:.5rem 0;color:#c4cfdf;font-size:.92rem}' +
        '.jf-watchdog-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:.65rem;margin:.9rem 0}.jf-watchdog-source{padding:.9rem;border:1px solid #344055;border-radius:12px;background:#ffffff05}' +
        '.jf-watchdog-source[data-active="true"]{border-color:#63c9ff;background:#63c9ff0a}.jf-watchdog-source strong{display:block;overflow-wrap:anywhere;font-size:1rem}.jf-watchdog-source small{display:block;margin-top:.4rem;color:#c4cfdf;line-height:1.5}' +
        '.jf-watchdog button{padding:.6rem 1rem;border-radius:9px;border:1px solid #58728c;color:#eef7ff;background:#20344a;cursor:pointer;font:inherit}.jf-watchdog button:disabled{opacity:.6}' +
        '.jf-watchdog .jf-watchdog-recommend{border-left:3px solid #ffcc78;background:#ffcc780d;padding:.8rem 1rem;color:#ffdda4}.jf-watchdog-actions{display:flex;gap:1rem;flex-wrap:wrap;align-items:center}';
    document.head.appendChild(style);

    function de() { return /^de/i.test(document.documentElement.lang || navigator.language || ''); }
    function tr(german, english) { return de() ? german : english; }
    function node(tag, cls, text) {
        var n = document.createElement(tag);
        if (cls) { n.className = cls; }
        if (text != null) { n.textContent = text; }
        return n;
    }
    function identity() {
        var api = window.ApiClient;
        return api && api.serverAddress && api.getCurrentUserId ? api.serverAddress() + '|' + api.getCurrentUserId() : '';
    }
    function request(path) {
        var api = window.ApiClient;
        var timeout;
        return Promise.race([api.getJSON(api.getUrl(path)), new Promise(function (resolve, reject) {
            timeout = window.setTimeout(function () { reject(new Error('Timeout')); }, 10000);
        })]).then(function (result) { window.clearTimeout(timeout); return result; }, function (error) { window.clearTimeout(timeout); throw error; });
    }
    function display(host) {
        if (!snapshot || admin !== true) { return; }
        var panel = host.querySelector('.jf-watchdog');
        if (panel && panel._snapshot === snapshot) { return; }
        var expanded = panel && panel.open;
        var focused = panel && panel.contains(document.activeElement) ? document.activeElement.getAttribute('data-control') : null;
        var fresh = node('details', 'jf-watchdog');
        fresh.open = !!expanded;
        fresh._snapshot = snapshot;
        var states = {
            Disabled: tr('Prüfungen pausiert', 'Checks paused'), Checking: tr('Prüfung läuft', 'Checking'),
            Stopping: tr('Prüfung wird beendet', 'Stopping current check'),
            PlaybackOrRecording: tr('Wiedergabe hat Vorrang', 'Playback takes priority'), PlaybackCooldown: tr('Wartezeit nach Wiedergabe', 'Playback cooldown'),
            Waiting: tr('Bereit für Leerlaufprüfung', 'Waiting for idle time')
        };
        var summary = node('summary', '', 'IPTV-Watchdog · ' + (states[snapshot.State] || states.Waiting));
        summary.setAttribute('data-control', 'summary');
        fresh.appendChild(summary);
        var body = node('div', 'jf-watchdog-body');
        body.appendChild(node('p', '', tr('Ein kurzer Medientest pro Minute im Leerlauf. Favoriten, zuletzt verwendete Sender und Ersatzserver werden abwechselnd geprüft. Statuswerte gelten für Stichproben, nicht als Verfügbarkeitsgarantie.',
            'One short media test per idle minute. Favorites, recent channels and backup servers rotate. Results describe samples, not guaranteed availability.')));
        (snapshot.Tuners || []).forEach(function (tuner) {
            if ((tuner.SharedDestinations || []).length) {
                body.appendChild(node('p', 'jf-watchdog-recommend', tr('Mehrere Einstiegsadressen führen zu denselben Auslieferungsservern: ', 'Several entry addresses lead to the same delivery servers: ')
                    + tuner.SharedDestinations.join(', ') + tr('. Das sind keine nachgewiesen unabhängigen Ersatzquellen.', '. These are not proven independent backup sources.')));
            }
            if (tuner.AccountReason) {
                body.appendChild(node('p', 'jf-watchdog-recommend', tuner.AccountReason === 'ProviderBusy'
                    ? tr('Anbieter ausgelastet: Prüfungen und Serverempfehlungen pausieren zehn Minuten.', 'Provider busy: checks and recommendations pause for ten minutes.')
                    : tr('Anbieter-Anmeldung fehlgeschlagen. Zugang prüfen; Sender werden deshalb nicht als ausgefallen markiert.', 'Provider authentication failed. Check the account; channels are not marked unavailable for this.')));
            }
            var recommended = (tuner.Sources || []).filter(function (s) { return s.Id === tuner.RecommendedSourceId; })[0];
            if (recommended) {
                body.appendChild(node('p', 'jf-watchdog-recommend', tr('Serverwechsel vorgeschlagen: ', 'Suggested server change: ') + recommended.Host + ' · '
                    + (tuner.RecommendationReason === 'SlowStart' ? tr('kürzere Startzeiten bei denselben Sendern.', 'shorter startup on the same channels.') : tr('weniger Ausfälle bei denselben Sendern.', 'fewer failures on the same channels.'))
                    + tr(' Der aktive Server bleibt unverändert; die Auswahl erfolgt in den Live-TV-Tunereinstellungen.', ' The primary remains unchanged; select the source in Live TV tuner settings.')));
            }
            var grid = node('div', 'jf-watchdog-grid');
            (tuner.Sources || []).forEach(function (source) {
                var card = node('div', 'jf-watchdog-source');
                card.setAttribute('data-active', source.Active ? 'true' : 'false');
                card.appendChild(node('strong', '', source.Host));
                card.appendChild(node('small', '', source.Scheme.toUpperCase() + (source.Active ? tr(' · aktiv', ' · active') : tr(' · Alternative', ' · alternative'))));
                card.appendChild(node('small', '', source.Samples ? source.Samples + tr(' Beobachtungen · ', ' observations · ') + source.Channels + tr(' Sender', ' channels') : tr('Noch nicht geprüft', 'Not checked yet')));
                if (source.SuccessRate != null) {
                    card.appendChild(node('small', '', Math.round(source.SuccessRate * 100) + tr(' % ohne Ausfall · ', '% without failure · ') + source.VerifiedChannels + tr(' Sender mit Wiedergabenachweis', ' channels with playback evidence')));
                }
                if (source.MedianStartMilliseconds != null) {
                    card.appendChild(node('small', '', tr('Decodierter Start (Median): ', 'Decoded startup (median): ') + (source.MedianStartMilliseconds / 1000).toFixed(1) + ' s'));
                }
                if (source.LastCheckedUtc) {
                    card.appendChild(node('small', '', tr('Zuletzt: ', 'Last: ') + new Date(source.LastCheckedUtc).toLocaleString(de() ? 'de-DE' : 'en-GB')));
                }
                if ((source.DestinationHosts || []).length) {
                    card.appendChild(node('small', '', tr('Auslieferung: ', 'Delivery: ') + source.DestinationHosts.join(', ')));
                }
                grid.appendChild(card);
            });
            body.appendChild(grid);
        });
        body.appendChild(node('p', '', tr('Vergleichsfenster: 6 Stunden. Ein automatischer Fallback darf nur einen Server verwenden, auf dem genau dieser Sender erfolgreich decodiert oder abgespielt wurde. Laufende Wiedergaben werden für Vergleiche nicht umgeschaltet.',
            'Comparison window: 6 hours. Automatic fallback requires successful decoding or playback of the same channel on that server. Comparisons never switch a working stream.')));
        var actions = node('div', 'jf-watchdog-actions');
        var toggle = node('button', '', snapshot.IdleChecksEnabled ? tr('Prüfungen pausieren', 'Pause checks') : tr('Prüfungen aktivieren', 'Enable checks'));
        toggle.type = 'button';
        toggle.setAttribute('data-control', 'toggle');
        toggle.addEventListener('click', function () {
            var token = generation;
            var current = identity();
            var enabled = !snapshot.IdleChecksEnabled;
            toggle.disabled = true;
            request('System/Configuration/livetv').then(function (config) {
                if (token !== generation || identity() !== current) { return; }
                config.EnableChannelHealthProbes = enabled;
                return window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('System/Configuration/livetv'), data: JSON.stringify(config), contentType: 'application/json' });
            }).then(function () {
                if (token === generation) { nextRefresh = 0; schedule(); }
            }).catch(function () { toggle.textContent = tr('Speichern fehlgeschlagen – erneut versuchen', 'Save failed – try again'); })
                .then(function () { toggle.disabled = false; });
        });
        actions.appendChild(toggle);
        actions.appendChild(node('span', '', snapshot.SweepEnabled ? tr('Weitere Sender werden zyklisch geprüft.', 'Other channels are checked in rotation.') : tr('Prüfung auf bevorzugte Sender begrenzt.', 'Checks limited to preferred channels.')));
        body.appendChild(actions);
        body.appendChild(node('p', '', tr('Prüfungen pausieren, wenn derselbe IPTV-Zugang auf einem Gerät außerhalb Jellyfin benutzt wird.', 'Pause checks when the same IPTV account is in use outside Jellyfin.')));
        fresh.appendChild(body);
        if (panel) { host.replaceChild(fresh, panel); } else { host.insertBefore(fresh, host.firstChild); }
        if (focused) { var restore = fresh.querySelector('[data-control="' + focused + '"]'); if (restore) { restore.focus(); } }
    }
    function refresh() {
        scheduled = 0;
        var current = identity();
        if (current !== scope) {
            scope = current; generation++; pending = false; nextRefresh = 0; snapshot = null; admin = null;
            Array.prototype.forEach.call(document.querySelectorAll('.jf-watchdog'), function (p) { p.remove(); });
        }
        var host = document.getElementById('jf-livetv-overview');
        if (!host || !current || document.hidden || !host.getClientRects().length) { return; }
        display(host);
        if (pending || Date.now() < nextRefresh || admin === false) { return; }
        pending = true;
        var token = generation;
        var user = admin === true ? Promise.resolve(true) : request('Users/' + window.ApiClient.getCurrentUserId()).then(function (u) { return !!(u.Policy && u.Policy.IsAdministrator); });
        user.then(function (allowed) {
            if (token !== generation || current !== identity()) { return; }
            admin = allowed;
            if (allowed) { return request('LiveTv/Watchdog'); }
        }).then(function (data) {
            if (token !== generation || current !== identity()) { return; }
            if (data) { snapshot = data; display(host); }
            nextRefresh = Date.now() + 30000;
        }).catch(function () { if (token === generation) { nextRefresh = Date.now() + 60000; } })
            .then(function () { if (token === generation) { pending = false; } });
    }
    function schedule() { if (!scheduled) { scheduled = window.setTimeout(refresh, 150); } }
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('hashchange', schedule);
    window.addEventListener('popstate', schedule);
    document.addEventListener('visibilitychange', schedule);
    window.setInterval(schedule, 30000);
    schedule();
}());
