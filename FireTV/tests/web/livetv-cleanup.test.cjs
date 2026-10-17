const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');

function setup(t) {
    const dom = new JSDOM('<html><body></body></html>', { url: 'http://jellyfin.test/web/#/home', runScripts: 'outside-only' });
    const w = dom.window, clock = FakeTimers.withGlobal(w).install();
    const requests = [], player = { isLocalPlayer: true };
    let finishStop, current = player;
    const stopped = new Promise(resolve => { finishStop = resolve; });
    w.ApiClient = {
        getCurrentUserId: () => 'user', getUrl: route => 'http://jellyfin.test/' + route,
        accessToken: () => 'private-test', deviceId: () => 'browser',
        appName: () => 'Jellyfin%20Web', appVersion: () => '13.0.0', deviceName: () => 'Browser',
        reportPlaybackStopped: info => { requests.push(['stop', info]); return stopped; }
    };
    const original = w.ApiClient.reportPlaybackStopped;
    w.addEventListener('pagehide', () => { if (w.clearPlayerOnExit) current = null; });
    w.fetch = (url, options) => { requests.push(['fetch', url, options]); return Promise.resolve(); };
    w.playbackManager = {
        play: () => Promise.resolve(),
        getCurrentPlayer: () => current, currentItem: () => ({ Id: 'old', Type: 'TvChannel' }),
        currentMediaSource: () => ({ Id: 'source', LiveStreamId: 'live', IsInfiniteStream: true }),
        playSessionId: () => 'old-session',
        stop: () => { current = null; w.ApiClient.reportPlaybackStopped({ ItemId: 'old', PlaySessionId: 'old-session' }); return Promise.resolve(); }
    };
    w.eval(fs.readFileSync(path.resolve(__dirname, '../../../src/Jellyfin.LiveTv/Web/livetv-overview.js'), 'utf8'));
    t.after(() => { clock.uninstall(); w.close(); });
    return { w, clock, requests, player, finishStop, original };
}

test('channel handoff waits for the previous server stop, including after the local player disappears', async t => {
    const e = setup(t);
    e.w.FireTvLive = { play: () => e.requests.push(['play']) };
    e.w.NativePlayer = { loadPlayer: () => {} };
    const next = e.w.JellyfinLiveTvOverview.play({ Id: 'new', Type: 'TvChannel' });
    await e.clock.tickAsync(5);
    assert.equal(e.requests.filter(r => r[0] === 'play').length, 0);
    assert.equal(e.requests.filter(r => r[0] === 'stop').length, 1);
    e.finishStop();
    await next;
    assert.equal(e.requests.filter(r => r[0] === 'play').length, 1);
    assert.equal(e.w.ApiClient.reportPlaybackStopped, e.original);
});

test('page close reports only the exact local live session with keepalive', async t => {
    const e = setup(t); await e.clock.tickAsync(1);
    e.w.dispatchEvent(new e.w.Event('pagehide'));
    const request = e.requests.find(r => r[0] === 'fetch');
    assert.ok(request);
    assert.equal(request[1], 'http://jellyfin.test/Sessions/Playing/Stopped');
    assert.equal(request[2].keepalive, true);
    assert.match(request[2].headers.Authorization, /Client="Jellyfin%20Web"/);
    assert.match(request[2].headers.Authorization, /DeviceId="browser"/);
    assert.deepEqual(JSON.parse(request[2].body), { ItemId: 'old', MediaSourceId: 'source', PlaySessionId: 'old-session', LiveStreamId: 'live' });
    e.requests.length = 0;
    e.w.dispatchEvent(new e.w.Event('visibilitychange'));
    assert.equal(e.requests.length, 0);
    e.player.isLocalPlayer = false;
    e.w.dispatchEvent(new e.w.Event('pagehide'));
    assert.equal(e.requests.length, 0);
    e.player.isLocalPlayer = true;
    e.w.NativePlayer = { loadPlayer: () => {} };
    e.w.dispatchEvent(new e.w.Event('pagehide'));
    assert.equal(e.requests.length, 0);
});

test('page close captures the session before the web player teardown clears it', async t => {
    const e = setup(t); await e.clock.tickAsync(1);
    e.w.clearPlayerOnExit = true;
    e.w.dispatchEvent(new e.w.Event('pagehide'));
    assert.equal(e.requests.filter(r => r[0] === 'fetch').length, 1);
});

test('unconfirmed stop prevents a second channel and restores the report handler', async t => {
    const e = setup(t);
    let plays = 0;
    e.w.FireTvLive = { play: () => plays++ }; e.w.NativePlayer = { loadPlayer: () => {} };
    const next = e.w.JellyfinLiveTvOverview.play({ Id: 'new', Type: 'TvChannel' });
    await e.clock.tickAsync(13000);
    assert.equal(await next, 'error');
    assert.equal(plays, 0);
    assert.equal(e.w.ApiClient.reportPlaybackStopped, e.original);
});
