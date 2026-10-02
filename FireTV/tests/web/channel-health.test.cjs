const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const source = fs.readFileSync(path.resolve(__dirname, '../../../src/Jellyfin.LiveTv/Web/channel-health.js'), 'utf8');
const id = 'a'.repeat(32);

function setup(t, handler) {
    const dom = new JSDOM('<html lang="de"><body></body></html>', { url: 'http://jellyfin.test/web/#/livetv', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    const observers = [];
    const Observer = w.MutationObserver;
    w.MutationObserver = class extends Observer { constructor(callback) { super(callback); observers.push(this); } };
    const clock = FakeTimers.withGlobal(w).install({ now: Date.parse('2026-10-02T12:00:00Z'), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    const calls = [];
    let user = 'one';
    w.ApiClient = { serverAddress: () => 'http://jellyfin.test', getCurrentUserId: () => user,
        getUrl: (p, q) => p + '?' + new URLSearchParams(q),
        getJSON: url => { calls.push(url); return handler(url, w); }
    };
    const row = (type = 'TvChannel', client = 'web') => {
        w.document.body.innerHTML = `<button class="${client === 'web' ? 'jf-livetv-row' : 'firetv-live-row'}" data-type="${type}" data-id="${id}"><span class="${client === 'web' ? 'jf-livetv-sender' : 'firetv-live-name'}">Sender</span></button>`;
    };
    row();
    w.eval(source);
    t.after(() => { observers.forEach(o => o.disconnect()); clock.uninstall(); w.close(); });
    return { w, clock, calls, row, badge: () => w.document.querySelector('.jf-channel-health'), user: value => { user = value; } };
}

for (const client of ['web', 'firetv']) {
    test(client + ': displays shared status without opening playback or changing the channel name', async t => {
        const e = setup(t, () => Promise.resolve({ [id]: { Status: 'Healthy', LastCheckedUtc: '2026-10-02T12:00:00Z' } }));
        e.row('TvChannel', client);
        await e.clock.tickAsync(300);
        assert.equal(e.badge().getAttribute('data-state'), 'Healthy');
        assert.match(e.badge().textContent, /Zuletzt erreichbar.*geprüft/);
        assert.match(e.w.document.body.textContent, /^Sender/);
        assert.equal(e.calls.length, 1);
        assert.ok(e.calls.every(url => url.startsWith('LiveTv/ChannelHealth?')));
    });
}

test('group folders never receive a channel health badge', async t => {
    const e = setup(t, () => Promise.resolve({}));
    e.row('Folder');
    await e.clock.tickAsync(300);
    assert.equal(e.badge(), null);
    assert.equal(e.calls.length, 0);
});

test('a client playback failure is visible even when the server received data', async t => {
    const e = setup(t, () => Promise.resolve({ [id]: { Status: 'Unstable', Reason: 'ClientPlaybackFailed', BytesReceived: 100000 } }));
    await e.clock.tickAsync(300);
    assert.equal(e.badge().getAttribute('data-state'), 'Unstable');
    assert.equal(e.badge().textContent, 'Wiedergabe fehlgeschlagen');
});

test('provider account errors are shown separately and stale healthy data expires', async t => {
    let state = { Status: 'Unknown', Reason: 'ProviderBusy', LastCheckedUtc: '2026-10-02T12:00:00Z' };
    const e = setup(t, () => Promise.resolve({ [id]: state }));
    await e.clock.tickAsync(300);
    assert.match(e.badge().textContent, /Anbieter ausgelastet/);
    assert.equal(e.badge().getAttribute('data-state'), 'Unknown');
    state = { Status: 'Healthy', LastCheckedUtc: '2026-10-02T10:00:00Z' };
    await e.clock.tickAsync(31000);
    assert.match(e.badge().textContent, /Prüfung veraltet/);
    assert.equal(e.badge().getAttribute('data-state'), 'Unknown');
});

test('late status from another user cannot color a reused row', async t => {
    let resolve;
    const e = setup(t, () => e.calls.length === 1 ? new Promise(r => { resolve = r; }) : Promise.resolve({}));
    await e.clock.tickAsync(150);
    e.user('two');
    e.row();
    await e.clock.tickAsync(150);
    resolve({ [id]: { Status: 'Unavailable', LastCheckedUtc: '2026-10-02T12:00:00Z' } });
    await e.clock.tickAsync(150);
    assert.equal(e.badge().getAttribute('data-state'), 'Unknown');
});

test('an unresponsive status API times out and retries without preventing navigation', async t => {
    const e = setup(t, () => new Promise(() => {}));
    await e.clock.tickAsync(60000);
    assert.equal(e.calls.length, 2);
    assert.equal(e.badge().getAttribute('data-state'), 'Unknown');
    e.w.history.pushState({}, '', '#/movies');
    e.w.document.body.innerHTML = '<div>Filme</div>';
    await e.clock.tickAsync(200);
    assert.equal(e.badge(), null);
});
