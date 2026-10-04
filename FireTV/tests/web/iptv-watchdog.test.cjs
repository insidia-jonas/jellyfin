const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const script = fs.readFileSync(path.resolve(__dirname, '../../../src/Jellyfin.LiveTv/Web/iptv-watchdog.js'), 'utf8');

function setup(t, handler) {
    const dom = new JSDOM('<html lang="de"><body><div id="jf-livetv-overview"></div></body></html>', { url: 'http://jellyfin.test/web/#/livetv', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window;
    w.HTMLElement.prototype.getClientRects = function () { return [{}]; };
    const clock = FakeTimers.withGlobal(w).install({ now: Date.parse('2026-10-04T12:00:00Z'), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    let user = 'admin';
    const calls = [], writes = [];
    w.ApiClient = {
        serverAddress: () => 'http://jellyfin.test', getCurrentUserId: () => user, getUrl: p => p,
        getJSON: p => { calls.push(p); return handler(p); },
        ajax: p => { writes.push(p); return Promise.resolve(); }
    };
    w.eval(script);
    t.after(() => { clock.uninstall(); w.close(); });
    return { w, clock, calls, writes, user: value => { user = value; }, panel: () => w.document.querySelector('.jf-watchdog') };
}

function status() {
    return { IdleChecksEnabled: true, SweepEnabled: true, State: 'Waiting', Tuners: [{ Id: 'tuner', SharedDestinations: ['shared.example'], Sources: [{ Id: 'one', Host: '<img src=x onerror=alert(1)>', Scheme: 'https', Active: true, Samples: 3, Channels: 3, VerifiedChannels: 3, SuccessRate: 1, MedianStartMilliseconds: 1234, DestinationHosts: ['shared.example'] }] }] };
}

test('admin sees safe observations and shared CDN caveat without any playback requests', async t => {
    const e = setup(t, p => Promise.resolve(p.startsWith('Users/') ? { Policy: { IsAdministrator: true } } : status()));
    await e.clock.tickAsync(500);
    assert.ok(e.panel());
    assert.match(e.panel().textContent, /keine nachgewiesen unabhängigen Ersatzquellen/);
    assert.match(e.panel().textContent, /1.2 s/);
    assert.equal(e.panel().querySelector('img'), null);
    assert.deepEqual(e.calls, ['Users/admin', 'LiveTv/Watchdog']);
    assert.equal(e.writes.length, 0);
});

test('ordinary viewers never request source comparisons or configuration', async t => {
    const e = setup(t, () => Promise.resolve({ Policy: { IsAdministrator: false } }));
    await e.clock.tickAsync(61000);
    assert.equal(e.panel(), null);
    assert.deepEqual(e.calls, ['Users/admin']);
});

test('pause modifies only the idle switch in freshly loaded configuration', async t => {
    const e = setup(t, p => Promise.resolve(p.startsWith('Users/') ? { Policy: { IsAdministrator: true } }
        : p === 'System/Configuration/livetv' ? { EnableChannelHealthProbes: true, TunerHosts: [{ Id: 'preserve' }], ListingProviders: [{ Id: 'epg' }] } : status()));
    await e.clock.tickAsync(500);
    e.panel().querySelector('button').click();
    await e.clock.tickAsync(500);
    assert.equal(e.writes.length, 1);
    const saved = JSON.parse(e.writes[0].data);
    assert.equal(saved.EnableChannelHealthProbes, false);
    assert.deepEqual(saved.TunerHosts, [{ Id: 'preserve' }]);
    assert.deepEqual(saved.ListingProviders, [{ Id: 'epg' }]);
});

test('late administrator response cannot render after another user signs in', async t => {
    let finish;
    const e = setup(t, p => p === 'Users/admin' ? new Promise(resolve => { finish = resolve; }) : Promise.resolve({ Policy: { IsAdministrator: false } }));
    await e.clock.tickAsync(200);
    e.user('viewer');
    e.w.dispatchEvent(new e.w.Event('hashchange'));
    await e.clock.tickAsync(200);
    finish({ Policy: { IsAdministrator: true } });
    await e.clock.tickAsync(200);
    assert.equal(e.panel(), null);
    assert.ok(!e.calls.includes('LiveTv/Watchdog'));
});

test('refresh keeps the expanded comparison open and restores focused controls', async t => {
    const e = setup(t, p => Promise.resolve(p.startsWith('Users/') ? { Policy: { IsAdministrator: true } } : status()));
    await e.clock.tickAsync(500);
    e.panel().open = true;
    e.panel().querySelector('button').focus();
    await e.clock.tickAsync(61000);
    assert.equal(e.panel().open, true);
    assert.equal(e.w.document.activeElement.getAttribute('data-control'), 'toggle');
});
