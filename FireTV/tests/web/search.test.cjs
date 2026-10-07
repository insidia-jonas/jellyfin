const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const root = path.resolve(__dirname, '../../..');
function setup(t, hash = '#/search?query=The%20Matrix%201999') {
    const dom = new JSDOM('<html><body><div class="page searchPage"><input type="search"><div class="searchResults"></div></div></body></html>', {
        url: 'http://jellyfin.test/web/' + hash, runScripts: 'outside-only', pretendToBeVisual: true
    });
    const w = dom.window;
    const clock = FakeTimers.withGlobal(w).install({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    const calls = [];
    const stock = [];
    w.ApiClient = {
        getCurrentUserId: () => 'user', serverAddress: () => 'http://jellyfin.test', accessToken: () => 'test-token',
        getUrl: (p, args) => 'http://jellyfin.test/' + p + '?' + new URLSearchParams(args),
        getItems: (user, options) => { stock.push(options); return Promise.resolve({ Items: [{ Id: 'one', Name: 'Matrix' }], TotalRecordCount: 92 }); }
    };
    w.fetch = (url, options) => new Promise(resolve => calls.push({ url, options, resolve: data => resolve({ ok: true, json: () => Promise.resolve(data) }) }));
    t.after(() => { clock.uninstall(); w.close(); });
    return { w, clock, calls, stock, input: w.document.querySelector('input'), load: file => w.eval(fs.readFileSync(path.join(root, file), 'utf8')) };
}
const experience = 'FireTV/app/src/main/assets/native/tvExperience.js';

test('one exact search preserves the year and does not repeat when the input is rebound', async t => {
    const e = setup(t); e.input.value = 'The Matrix 1999'; e.load(experience);
    await e.clock.tickAsync(1);
    assert.equal(e.calls.length, 1);
    assert.equal(e.calls[0].options.headers.Authorization, 'MediaBrowser Token="test-token"');
    assert.equal(new URL(e.calls[0].url).searchParams.get('SearchTerm'), 'The Matrix 1999');
    assert.equal(new URL(e.calls[0].url).searchParams.has('ParentId'), false);
    e.calls[0].resolve({ Items: [{ Id: 'matrix', Name: 'The Matrix' }] });
    await e.clock.tickAsync(10000);
    assert.equal(e.calls.length, 1);
    assert.equal(e.w.document.querySelector('.firetv-card-title').textContent, 'The Matrix');
});

test('typing cancels the old request and a late reply cannot replace the current result', async t => {
    const e = setup(t); e.input.value = 'Matrix'; e.load(experience); await e.clock.tickAsync(1);
    e.input.value = 'Alien'; e.input.dispatchEvent(new e.w.Event('input'));
    assert.equal(e.calls[0].options.signal.aborted, true);
    await e.clock.tickAsync(400);
    assert.equal(e.calls.length, 2);
    e.calls[1].resolve({ Items: [{ Id: 'alien', Name: 'Alien' }] }); await e.clock.tickAsync(1);
    e.calls[0].resolve({ Items: [{ Id: 'matrix', Name: 'Matrix' }] }); await e.clock.tickAsync(1);
    assert.equal(e.w.document.querySelector('.firetv-card-title').textContent, 'Alien');
});

test('leaving search cancels work and rejects a late response', async t => {
    const e = setup(t); e.input.value = 'Matrix'; e.load(experience); await e.clock.tickAsync(1);
    e.w.location.hash = '#/home'; await e.clock.tickAsync(1);
    assert.equal(e.calls[0].options.signal.aborted, true);
    e.calls[0].resolve({ Items: [{ Id: 'old', Name: 'Old result' }] }); await e.clock.tickAsync(1);
    assert.equal(e.w.document.querySelectorAll('.firetv-card').length, 0);
});

test('stock search keeps its scope and total count without an extra fallback request', async t => {
    const e = setup(t); e.load(experience); await e.clock.tickAsync(1);
    const result = await e.w.ApiClient.getItems('user', { SearchTerm: 'Matrix', ParentId: 'library', Recursive: false });
    assert.equal(e.stock.length, 1);
    assert.equal(e.stock[0].ParentId, 'library'); assert.equal(e.stock[0].Recursive, false);
    assert.equal(result.TotalRecordCount, 92);
});

test('other page filters never start global search', async t => {
    const e = setup(t, '#/dashboard'); e.input.value = 'Matrix'; e.load(experience);
    e.input.dispatchEvent(new e.w.Event('input')); await e.clock.tickAsync(5000);
    assert.equal(e.calls.length, 0); assert.equal(e.stock.length, 0);
});

test('Treasure Maps does not duplicate the Fire TV search endpoint', async t => {
    const e = setup(t); e.w.FireTvSmartSearch = true;
    e.load('plugins/Jellyfin.Plugin.TreasureMaps/Web/treasuremaps.js');
    await e.clock.tickAsync(1000);
    assert.equal(e.calls.length, 0);
});

test('Treasure Maps rejects responses from a previous search route', async t => {
    const e = setup(t);
    e.load('plugins/Jellyfin.Plugin.TreasureMaps/Web/treasuremaps.js'); await e.clock.tickAsync(1000);
    assert.equal(e.calls.length, 1);
    e.w.location.hash = '#/search?query=Alien'; await e.clock.tickAsync(1000);
    assert.equal(e.calls[0].options.signal.aborted, true);
    assert.equal(e.calls.length, 2);
    e.calls[1].resolve({ ok: true, items: [{ id: 'alien', name: 'Alien' }] }); await e.clock.tickAsync(1);
    e.calls[0].resolve({ ok: true, items: [{ id: 'matrix', name: 'Matrix' }] }); await e.clock.tickAsync(1);
    assert.equal(e.w.document.querySelector('.tmSearchName').textContent, 'Alien');
});

test('Indexer search distinguishes loading, no matches and an unavailable provider with retry', async t => {
    const e = setup(t); e.load('plugins/Jellyfin.Plugin.TreasureMaps/Web/treasuremaps.js'); await e.clock.tickAsync(1000);
    assert.match(e.w.document.querySelector('.tmSearchStatus').textContent, /durchsucht/);
    e.calls[0].resolve({ ok: false, items: [] }); await e.clock.tickAsync(1);
    assert.match(e.w.document.querySelector('.tmSearchStatus').textContent, /nicht erreichbar/);
    e.w.document.querySelector('#tmSearchHits button').click(); await e.clock.tickAsync(1000);
    assert.equal(e.calls.length, 2); e.calls[1].resolve({ ok: true, items: [] }); await e.clock.tickAsync(1);
    assert.match(e.w.document.querySelector('.tmSearchStatus').textContent, /Keine passenden Titel/);
    assert.equal(e.w.document.querySelector('#tmSearchHits').getAttribute('aria-busy'), 'false');
});




test('dashboard ignores superseded searches and appends deduplicated pages', async t => {
    const html = fs.readFileSync(path.join(root, 'plugins/Jellyfin.Plugin.TreasureMaps/Configuration/browse.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://jellyfin.test/web/#/configurationpage', runScripts: 'outside-only' });
    const w = dom.window;
    const calls = [];
    w.ApiClient = { accessToken: () => 'test-token', getUrl: (p, q) => 'http://jellyfin.test/' + p + '?' + new URLSearchParams(q) };
    w.fetch = (url, options) => new Promise(resolve => calls.push({ url, options, resolve: data => resolve({ ok: true, json: () => Promise.resolve(data) }) }));
    t.after(() => w.close());
    w.eval(w.document.querySelector('script').textContent);
    const settle = () => new Promise(resolve => setImmediate(resolve));
    w.document.querySelector('[data-type="movie"]').click();
    w.document.querySelector('[data-type="tv"]').click();
    assert.equal(calls[0].options.signal.aborted, true);
    assert.equal(calls[1].options.headers.Authorization, 'MediaBrowser Token="test-token"');
    calls[1].resolve({ ok: true, items: [{ guid: 'first', title: 'Series' }], hasMore: true, nextOffset: 100 });
    await settle();
    calls[0].resolve({ ok: true, items: [{ guid: 'old', title: 'Old movie' }] });
    await settle();
    assert.equal(w.document.querySelector('.tmTitle').textContent, 'Series');
    w.document.querySelector('#tmMore').click();
    assert.equal(new URL(calls[2].url).searchParams.get('offset'), '100');
    calls[2].resolve({ ok: true, items: [{ guid: 'first', title: 'Series' }, { guid: 'second', title: 'Next' }], hasMore: false, nextOffset: 102 });
    await settle();
    assert.equal(w.document.querySelectorAll('.tmCard').length, 2);
    assert.equal(w.document.querySelector('#tmMore').hidden, true);
});
