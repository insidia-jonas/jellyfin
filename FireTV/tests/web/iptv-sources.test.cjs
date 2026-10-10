const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const script = fs.readFileSync(path.resolve(__dirname, '../../../src/Jellyfin.LiveTv/Web/iptv-sources.js'), 'utf8');
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function setup(t, pending = false) {
    const dom = new JSDOM('<html lang="de"><head></head><body><div class="videoOsdBottom"><div class="buttons"></div></div></body></html>', { url: 'http://jellyfin.test', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window, clock = FakeTimers.withGlobal(w).install();
    let user = 'one', playing = true;
    const stop = deferred(), load = deferred(), calls = [];
    const info = { AutomaticMediaSourceId: 'base', PlayingSourceId: 'am02', Sources: [
        { Id: 'am02', Name: 'AM02 · Amsterdam', MediaSourceId: 'base_iptv_am02', Status: 'Reachable', ChannelSpecific: true, MedianStartMilliseconds: 1200, LastCheckedUtc: '2026-10-10T10:00:00Z' },
        { Id: 'ro01', Name: 'RO01 · Romania', MediaSourceId: 'base_iptv_ro01', Status: 'Unknown' }
    ] };
    w.ApiClient = { getCurrentUserId: () => user, serverAddress: () => 'http://server', getUrl: (p, q) => p + '?' + new URLSearchParams(q),
        getJSON: url => { calls.push(['get', url]); return pending ? load.promise : Promise.resolve(info); },
        reportPlaybackStopped: data => { calls.push(['stop-report', data]); return stop.promise; }
    };
    const original = w.ApiClient.reportPlaybackStopped;
    const pm = { currentItem: () => playing ? { Id: 'channel', Name: 'Sky Cinema Action', Type: 'TvChannel' } : null,
        currentMediaSource: () => ({ Id: 'base', LiveStreamId: 'live' }),
        stop: () => { playing = false; w.ApiClient.reportPlaybackStopped({ ItemId: 'channel', LiveStreamId: 'live' }); return Promise.resolve(); },
        play: options => { calls.push(['play', options]); return Promise.resolve(); }
    };
    w.JellyfinLiveTvOverview = { playerState: () => null, resolvePlaybackManager: () => pm };
    w.eval(script);
    t.after(() => { clock.uninstall(); dom.window.close(); });
    return { w, clock, calls, stop, load, info, original, account: value => { user = value; }, open: async () => { w.JellyfinIptvSources.open(); await clock.tickAsync(1); }, rows: () => [...w.document.querySelectorAll('[data-source]')] };
}
test('server menu reads only cached observations on demand and keeps keyboard focus across polling', async t => {
    const e = setup(t);
    await e.clock.tickAsync(2000);
    assert.equal(e.calls.length, 0);
    await e.open();
    assert.equal(e.rows().length, 3);
    assert.match(e.rows()[1].textContent, /Läuft jetzt.*1,2 s/);
    assert.match(e.rows()[2].textContent, /Ungeprüft/);
    e.rows()[2].focus();
    await e.clock.tickAsync(30000);
    assert.equal(e.w.document.activeElement.dataset.source, 'base_iptv_ro01');
    assert.ok(e.calls.every(c => c[0] === 'get' && c[1].includes('/Sources?')));
    e.w.document.dispatchEvent(new e.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    assert.equal(e.w.document.querySelector('.jf-iptv-drawer'), null);
});
test('manual switch waits for the exact stop report and starts the same channel once', async t => {
    const e = setup(t); await e.open(); e.rows()[2].click();
    await e.clock.tickAsync(500);
    assert.equal(e.calls.filter(c => c[0] === 'play').length, 0);
    e.stop.resolve(); await e.clock.tickAsync(1);
    const plays = e.calls.filter(c => c[0] === 'play');
    assert.equal(plays.length, 1);
    assert.equal(plays[0][1].mediaSourceId, 'base_iptv_ro01');
    assert.equal(plays[0][1].items[0].Id, 'channel');
    assert.equal(e.calls.filter(c => c[0] === 'stop-report').length, 1);
    assert.equal(e.w.ApiClient.reportPlaybackStopped, e.original);
});
test('back during closing the stream cancels the requested restart', async t => {
    const e = setup(t); await e.open(); e.rows()[1].click();
    e.w.JellyfinIptvSources.close(); e.stop.resolve(); await e.clock.tickAsync(1);
    assert.equal(e.calls.filter(c => c[0] === 'play').length, 0);
});
test('failed stop prevents another provider connection and offers visible error', async t => {
    const e = setup(t); await e.open(); e.rows()[1].click();
    e.stop.reject(new Error('stop failed')); await e.clock.tickAsync(1);
    assert.equal(e.calls.filter(c => c[0] === 'play').length, 0);
    assert.match(e.w.document.querySelector('.jf-iptv-status').textContent, /Wechsel fehlgeschlagen/);
    assert.equal(e.w.ApiClient.reportPlaybackStopped, e.original);
});
test('late menu response after an account change cannot populate the new account', async t => {
    const e = setup(t, true); await e.open(); e.account('two');
    await e.clock.tickAsync(1000); e.load.resolve(e.info); await e.clock.tickAsync(1);
    assert.equal(e.rows().length, 0);
});
