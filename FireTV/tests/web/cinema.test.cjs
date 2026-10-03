const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const script = fs.readFileSync(path.resolve(__dirname, '../../../plugins/Jellyfin.Plugin.TreasureMaps/Web/cinema.js'), 'utf8');
const movie = { Id: 'movie', Type: 'Movie', Name: 'Film', Overview: 'A story', BackdropImageTags: ['tag'], UserData: { PlaybackPositionTicks: 120000000 } };
function setup(t, hash = '#/home') {
    const dom = new JSDOM('<div id="homeTab" class="is-active"><div class="homeSectionsContainer">Native home</div></div><div id="moviesPage"></div>', { url: 'http://jellyfin.test/web/' + hash, runScripts: 'outside-only' });
    const w = dom.window, clock = FakeTimers.withGlobal(w).install();
    const calls = [], plays = [];
    const api = { getCurrentUserId: () => 'user', accessToken: () => 'private', serverId: () => 'server', getUrl: (p, q) => 'http://jellyfin.test/' + p + (q ? '?' + new URLSearchParams(q) : ''), getImageUrl: () => 'http://jellyfin.test/image' };
    w.ApiClient = api;
    w.fetch = (url, options) => { calls.push({ url, options }); return Promise.resolve({ ok: true, json: () => Promise.resolve({ Items: [movie] }) }); };
    w.playbackManager = { play: options => { plays.push(options); return Promise.resolve(); } };
    t.after(() => { clock.uninstall(); w.close(); });
    return { w, clock, calls, plays, api, load: () => w.eval(script) };
}
test('home uses two bounded reads, deduplicates titles, and resumes through native playback', async t => {
    const e = setup(t); e.load(); await e.clock.tickAsync(1000);
    assert.equal(e.calls.length, 2);
    assert.match(e.calls[0].url, /Limit=3/); assert.match(e.calls[1].url, /Limit=8/);
    assert.equal(e.w.document.querySelectorAll('.cinema-hero').length, 1);
    assert.equal(e.w.document.querySelector('.cinema-hero-navigation').hidden, true);
    const actions = e.w.document.querySelector('.cinema-hero-actions');
    actions.notifyRefreshNeeded = () => {};
    actions.addEventListener('click', event => e.plays.push(event.target.dataset));
    e.w.document.querySelector('.cinema-primary').click(); await e.clock.tickAsync(1);
    assert.equal(e.plays.length, 1); assert.equal(e.plays[0].id, 'movie'); assert.equal(e.plays[0].positionticks, '120000000');
    assert.equal(e.plays[0].serverid, 'server'); assert.equal(e.plays[0].action, 'resume');
    await e.clock.tickAsync(20000); assert.equal(e.calls.length, 2, 'idle UI does not poll library/provider data');
});
test('initial login readiness, anonymous and dashboard routes never expose a hero', async t => {
    const e = setup(t); e.w.ApiClient = null; e.load(); await e.clock.tickAsync(1500);
    assert.equal(e.calls.length, 0); assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    e.w.ApiClient = e.api; await e.clock.tickAsync(1000); assert.equal(e.calls.length, 2);
    e.w.history.pushState({}, '', '#/configurationpage?name=TreasureMapsManagement');
    assert.equal(e.w.document.documentElement.classList.contains('cinema-ui'), false);
    assert.equal(e.w.document.querySelector('.cinema-hero'), null);
});
test('History API transitions cancel slow reads and cannot decorate another route', async t => {
    const e = setup(t); let finish;
    e.w.fetch = (url, options) => { e.calls.push({ url, options }); return new Promise(r => { finish = r; }); };
    e.load(); await e.clock.tickAsync(1);
    e.w.history.pushState({}, '', '#/list?parentId=treasure');
    assert.equal(e.calls[0].options.signal.aborted, true);
    finish({ ok: true, json: () => Promise.resolve({ Items: [movie] }) }); await e.clock.tickAsync(10000);
    assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    assert.equal(e.w.document.documentElement.classList.contains('cinema-home'), false);
});
test('logout and user changes discard cached media', async t => {
    const e = setup(t); e.load(); await e.clock.tickAsync(1000);
    e.api.accessToken = () => ''; await e.clock.tickAsync(1000);
    assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    e.api.accessToken = () => 'other'; e.api.getCurrentUserId = () => 'restricted';
    e.w.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({ Items: [] }) });
    await e.clock.tickAsync(1000); assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    assert.match(e.w.document.querySelector('.homeSectionsContainer').textContent, /Native home/);
});
test('native home survives failure, empty libraries and timeout without retry storms', async t => {
    const e = setup(t); e.w.fetch = (url, options) => { e.calls.push({ url, options }); return Promise.reject(new Error('offline')); };
    e.load(); await e.clock.tickAsync(30000);
    assert.equal(e.calls.length, 2); assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    assert.match(e.w.document.querySelector('.homeSectionsContainer').textContent, /Native home/);
});

test('slow hero requests abort after eight seconds while the native page stays usable', async t => {
    const e = setup(t);
    e.w.fetch = (url, options) => { e.calls.push({ url, options }); return new Promise(() => {}); };
    e.load(); await e.clock.tickAsync(8001);
    assert.equal(e.calls.length, 2); assert.equal(e.calls.every(x => x.options.signal.aborted), true);
    assert.match(e.w.document.querySelector('.homeSectionsContainer').textContent, /Native home/);
    await e.clock.tickAsync(30000); assert.equal(e.calls.length, 2);
});
test('untrusted metadata is plain text and channel/virtual cards cannot be played in the hero', async t => {
    const e = setup(t);
    e.w.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({ Items: [
        { ...movie, Id: 'channel', ChannelId: 'tm' }, { ...movie, Id: 'virtual', IsVirtualItem: true },
        { ...movie, Name: '<img onerror="alert(1)">', Overview: '<b>Safe text</b><img onerror="alert(1)">' }
    ] }) });
    e.load(); await e.clock.tickAsync(1000);
    assert.equal(e.w.document.querySelector('.cinema-hero-title').textContent, '<img onerror="alert(1)">');
    assert.equal(e.w.document.querySelector('.cinema-hero-title img'), null);
    assert.equal(e.w.document.querySelector('.cinema-hero-overview').textContent, 'Safe text');
});
test('cached return home preserves native DOM and carousel controls retain focus', async t => {
    const e = setup(t);
    e.w.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({ Items: [movie, { ...movie, Id: 'second', Name: 'Second', Type: 'Series' }] }) });
    e.load(); await e.clock.tickAsync(1000);
    const button = e.w.document.querySelector('[aria-label="Nächster Titel"]'); button.focus(); button.click();
    assert.equal(e.w.document.activeElement, button); assert.equal(e.w.document.querySelector('.cinema-primary').hidden, true);
    e.w.history.pushState({}, '', '#/details?id=second'); assert.equal(e.w.document.querySelector('.cinema-hero'), null);
    e.w.history.pushState({}, '', '#/home'); assert.equal(e.w.document.querySelectorAll('.cinema-hero').length, 1);
    assert.match(e.w.document.querySelector('.homeSectionsContainer').textContent, /Native home/);
});
test('library search preserves parent scope and decorative changes release on settings', async t => {
    const e = setup(t, '#/movies?topParentId=library'); e.load(); await e.clock.tickAsync(1000);
    assert.equal(e.w.document.querySelector('.cinema-library-lead a').getAttribute('href'), '#/search?parentId=library');
    assert.equal(e.calls.length, 0);
    e.w.history.replaceState({}, '', '#/userpreferences');
    assert.equal(e.w.document.querySelector('.cinema-library-lead'), null);
    assert.equal(e.w.document.documentElement.classList.contains('cinema-ui'), false);
});
