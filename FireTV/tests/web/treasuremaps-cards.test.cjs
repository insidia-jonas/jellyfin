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

test('versions are grouped by language with German first and other languages collapsed', async t => {
    const e = setup(t);
    e.api.getItems = () => Promise.resolve({ Items: [
        { Id: 'en', Name: 'English', ProviderIds: { TreasureMaps: 'en', TreasureMapsLanguages: 'en' } },
        { Id: 'multi', Name: 'Dual', ProviderIds: { TreasureMaps: 'multi', TreasureMapsLanguages: 'en,de' } },
        { Id: 'unknown', Name: '1080p', ProviderIds: { TreasureMaps: 'unknown' } },
        { Id: 'de', Name: 'German', ProviderIds: { TreasureMaps: 'de', TreasureMapsLanguages: 'de' } }
    ] });
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const groups = [...e.w.document.querySelectorAll('.tmLanguageGroup')];
    assert.equal(groups.length, 4);
    assert.match(groups[0].textContent, /Deutsch/);
    assert.equal(groups[0].open, true);
    assert.equal(groups.slice(1).some(x => x.open), false);
    assert.equal(e.w.document.querySelectorAll('.tmRelRow').length, 4, 'multilingual releases appear once');
    assert.equal(groups[1].querySelector('summary').tabIndex, 0);
});

test('leaving a download page releases styles before the category reuses its DOM', async t => {
    const e = setup(t); e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const page = e.w.document.querySelector('.page');
    page.classList.add('tmDownloadsPage');
    const downloads = e.w.document.createElement('div'); downloads.id = 'tmDownloads'; page.appendChild(downloads);
    e.w.history.pushState({}, '', '#/list?parentId=' + 'b'.repeat(32));
    assert.equal(page.classList.contains('tmDownloadsPage'), false);
    assert.equal(e.w.document.getElementById('tmDownloads'), null);
    assert.equal(page.querySelector('.tmNativeChildren'), null);
    await e.clock.tickAsync(1000);
    assert.equal(page.classList.contains('tmTitlePage'), false, 'old observers cannot reapply title styles');
});

test('late successful grab cannot resume polling or overwrite the next category', async t => {
    const e = setup(t); let finish; let statusCalls = 0;
    e.api.fetch = options => {
        if (options.type === 'POST') { return new Promise(r => { finish = r; }); }
        statusCalls++; return Promise.resolve({ ok: true, items: [] });
    };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    e.w.document.querySelector('.tmDl').click();
    e.w.history.pushState({}, '', '#/home');
    const before = statusCalls;
    finish({ ok: true, nzoIds: ['job'] }); await e.clock.tickAsync(7000);
    assert.equal(statusCalls, before);
    assert.equal(e.w.document.querySelector('#tmReleases'), null);
});

test('downloads load status rows and release their page when navigating to a category', async t => {
    const e = setup(t);
    e.api.getItem = () => Promise.resolve({ Id: id, Name: 'Downloads', ChannelId: 'channel' });
    e.api.getItems = () => Promise.resolve({ Items: [{ Id: 'download', Name: 'Movie' }] });
    e.api.fetch = () => Promise.resolve({ items: [{ id: 'job', title: 'Movie', percent: 25, status: 'Downloading' }] });
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    assert.equal(e.w.document.querySelectorAll('#tmDownloads .tmDlRow').length, 1);
    assert.match(e.w.document.querySelector('#tmDownloads').textContent, /Movie/);
    e.api.getItem = () => Promise.resolve({ Id: 'b'.repeat(32), Name: 'Filme', ChannelId: 'channel' });
    e.w.history.pushState({}, '', '#/list?parentId=' + 'b'.repeat(32));
    await e.clock.tickAsync(1200);
    assert.equal(e.w.document.querySelector('.tmDownloadsPage'), null);
    assert.equal(e.w.document.querySelector('#tmDownloads'), null);
});

test('automatic requests use the title endpoint and clean up reused library detail pages', async t => {
    const e = setup(t); const calls = [];
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="detailSectionContent"></div>');
    e.api.getItem = () => Promise.resolve({ Id: id, Name: 'Series', Type: 'Series' });
    e.api.fetch = options => {
        calls.push(options);
        return Promise.resolve({ Enabled: true, Service: 'Sonarr', Monitored: options.type === 'POST', Message: 'Status' });
    };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const button = e.w.document.querySelector('#tmArrRequest button');
    assert.match(button.textContent, /Serie anfordern.*Sonarr/);
    button.click(); await e.clock.tickAsync(1);
    assert.equal(button.disabled, true);
    assert.equal(calls.filter(x => x.type === 'POST')[0].url, 'TreasureMaps/Requests/' + id);
    e.w.history.pushState({}, '', '#/home');
    assert.equal(e.w.document.querySelector('#tmArrRequest'), null);
});
