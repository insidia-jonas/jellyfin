const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const script = fs.readFileSync(path.resolve(__dirname, '../../../plugins/Jellyfin.Plugin.TreasureMaps/Web/treasuremaps.js'), 'utf8');
const id = 'a'.repeat(32);
function setup(t) {
    const dom = new JSDOM('<div class="page"><div class="detailPageSecondaryContainer"><div id="childrenCollapsible"><div class="childrenItemsContainer"></div></div><div id="castCollapsible"><div class="itemsContainer">Cast</div></div></div></div>', { url: 'http://jellyfin.test/web/#/details?id=' + id, runScripts: 'outside-only' });
    const w = dom.window;
    const clock = FakeTimers.withGlobal(w).install();
    const item = { Id: id, Name: 'Movie', Type: 'BoxSet', ChannelId: 'channel' };
    const api = { getCurrentUserId: () => 'user', getItem: () => Promise.resolve(item), getItems: () => Promise.resolve({ Items: [{ Id: 'release', Name: '1080p · WEB', ProviderIds: { TreasureMaps: 'guid' } }] }), getUrl: p => p, fetch: () => Promise.resolve({ items: [] }) };
    t.after(() => { clock.uninstall(); w.close(); });
    return { w, clock, api, load: () => w.eval(script) };
}
test('client waits for Jellyfin login readiness on its initial route', async t => {
    const e = setup(t); e.load(); await e.clock.tickAsync(1200);
    e.w.ApiClient = e.api; await e.clock.tickAsync(1200);
    assert.equal(e.w.document.querySelectorAll('#tmReleases .tmRelRow').length, 1);
});
test('release list stays outside hidden native children and preserves the cast', async t => {
    const e = setup(t); e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const host = e.w.document.querySelector('#tmReleases');
    assert.ok(host);
    assert.equal(host.closest('#childrenCollapsible,.tmNativeChildren'), null);
    assert.equal(e.w.document.querySelector('#castCollapsible .itemsContainer').style.display, '');
});
test('late title response cannot populate a different detail page', async t => {
    const e = setup(t); let finish;
    e.api.getItems = () => new Promise(resolve => { finish = resolve; });
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    e.w.location.hash = '#/home';
    finish({ Items: [{ Id: 'old', Name: '1080p', ProviderIds: { TreasureMaps: 'old' } }] });
    await e.clock.tickAsync(1200);
    assert.equal(e.w.document.querySelector('#tmReleases'), null);
});
test('failed detail query offers a working retry', async t => {
    const e = setup(t); let calls = 0;
    e.api.getItems = () => { calls++; return Promise.reject(new Error('offline')); };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    assert.match(e.w.document.querySelector('.tmLoadMessage').textContent, /nicht geladen/);
    e.w.document.querySelector('.tmRetry').click(); await e.clock.tickAsync(1);
    assert.equal(calls, 2);
});
