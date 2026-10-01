const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');
const assets = path.resolve(__dirname, '../../app/src/main/assets/native');

function setup(t, { hash = '#/livetv', items = 4, pending = false } = {}) {
    const dom = new JSDOM('<html class="layout-tv" lang="de"><body><div class="libraryPage"><h1>Live TV</h1><div class="itemsContainer"></div></div></body></html>', {
        url: 'http://jellyfin.test/web/' + hash, runScripts: 'outside-only', pretendToBeVisual: true
    });
    const w = dom.window;
    const observers = [];
    const Observer = w.MutationObserver;
    w.MutationObserver = class extends Observer {
        constructor(callback) { super(callback); observers.push(this); }
    };
    const clock = FakeTimers.withGlobal(w).install({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame', 'cancelAnimationFrame', 'Date'] });
    const channels = Array.from({ length: items }, (_, i) => ({ Id: 'ch' + i, Name: 'Sender ' + i, Type: 'TvChannel', ImageTags: { Primary: 'tag' } }));
    let resolve;
    const calls = { channels: 0, search: 0, guide: 0 };
    w.ApiClient = {
        getCurrentUserId: () => 'user', getUrl: (p, params) => p + '?' + new URLSearchParams(params), getImageUrl: id => '/Items/' + id + '/Images/Primary',
        getJSON: url => {
            if (url.startsWith('LiveTv/Programs')) { calls.guide++; return Promise.resolve({ Items: [] }); }
            calls.channels++;
            const offset = Number(new URL(url, 'http://jellyfin.test').searchParams.get('startIndex'));
            return pending ? new Promise(r => { resolve = r; }) : Promise.resolve({ Items: channels.slice(offset, offset + 200), TotalRecordCount: channels.length });
        },
        getItems: () => { calls.search++; return Promise.resolve({ Items: [] }); }
    };
    function load(name) { w.eval(fs.readFileSync(path.join(assets, name), 'utf8')); }
    t.after(() => { observers.forEach(o => o.disconnect()); clock.uninstall(); w.close(); });
    return { w, clock, calls, channels, load, resolve: () => resolve({ Items: channels }) };
}

test('Live TV settles without replacing rows or stealing focus', async t => {
    const { w, clock, calls, load } = setup(t);
    load('tvLive.js'); load('tvExperience.js');
    await clock.tickAsync(600);
    const rows = w.document.querySelectorAll('.firetv-live-row');
    assert.equal(rows.length, 4);
    rows[2].focus();
    let changes = 0;
    const observer = new w.MutationObserver(records => { changes += records.length; });
    observer.observe(w.document.getElementById('firetv-live'), { childList: true, subtree: true });
    await clock.tickAsync(5000);
    assert.equal(w.document.querySelectorAll('.firetv-live-row')[2], rows[2]);
    assert.equal(w.document.activeElement, rows[2]);
    assert.equal(changes, 0);
    assert.equal(calls.channels, 1);
});

test('sender filtering keeps the input and does not search all libraries', async t => {
    const { w, clock, calls, load } = setup(t);
    load('tvLive.js'); load('tvExperience.js');
    await clock.tickAsync(600);
    const input = w.document.querySelector('.firetv-live-search');
    input.focus(); input.value = 'Sender 2';
    input.dispatchEvent(new w.Event('input', { bubbles: true }));
    await clock.tickAsync(1200);
    assert.equal(w.document.querySelector('.firetv-live-search'), input);
    assert.equal(w.document.activeElement, input);
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 1);
    assert.equal(calls.search, 0);
});

test('late channel response cannot repaint the page after navigation', async t => {
    const env = setup(t, { pending: true });
    env.load('tvLive.js');
    await env.clock.tickAsync(1);
    env.w.location.hash = '#/movies';
    env.w.document.querySelector('h1').textContent = 'Filme';
    await env.clock.tickAsync(1);
    env.resolve();
    await env.clock.tickAsync(1000);
    assert.equal(env.w.document.querySelectorAll('.firetv-live-row').length, 0);
    assert.equal(env.w.document.documentElement.classList.contains('firetv-live-on'), false);
});

test('large channel lists remain bounded when scrolling and navigating past 800 channels', async t => {
    const { w, clock, load, calls } = setup(t, { items: 1200 });
    load('tvLive.js');
    await clock.tickAsync(2000);
    const count = w.document.querySelectorAll('.firetv-live-row').length;
    assert.ok(count > 0 && count <= 48, 'render at most 48 channels until more are requested, got ' + count);
    assert.equal(calls.channels, 6);
    const viewport = w.document.querySelector('.firetv-live-viewport');
    viewport.scrollTop = 130000;
    viewport.dispatchEvent(new w.Event('scroll'));
    await clock.tickAsync(200);
    const rows = w.document.querySelectorAll('.firetv-live-row');
    assert.ok(rows.length <= 48);
    assert.ok(Number(rows[0].getAttribute('data-id').slice(2)) > 800);
    const last = rows[rows.length - 1];
    const nextId = 'ch' + (Number(last.getAttribute('data-id').slice(2)) + 1);
    last.focus();
    last.dispatchEvent(new w.KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    assert.equal(w.document.activeElement.getAttribute('data-id'), nextId);
});

test('channel logos load lazily and failed images leave a visible fallback', async t => {
    const { w, clock, load } = setup(t);
    load('tvLive.js');
    await clock.tickAsync(100);
    const logo = w.document.querySelector('.firetv-live-logo');
    const img = logo.querySelector('img');
    assert.ok(img, 'use an image with an error handler, not an opaque background');
    assert.equal(img.getAttribute('loading'), 'lazy');
    img.dispatchEvent(new w.Event('error'));
    assert.ok(!img.isConnected || img.hidden);
    assert.ok(logo.textContent.trim());
});

test('temporarily empty home rows stay visible so lazy loading can populate them', async t => {
    const { w, clock, load } = setup(t, { hash: '#/home' });
    w.document.body.innerHTML = '<div class="homeSectionsContainer"><div class="verticalSection"><h2 class="sectionTitle">Filme</h2><div class="itemsContainer"></div></div></div>';
    load('tvExperience.js');
    await clock.tickAsync(1000);
    assert.notEqual(w.document.querySelector('.verticalSection').getAttribute('data-firetv-hidden'), '1');
});

test('TV styling preserves the stock contain rule for logos', t => {
    const { w } = setup(t);
    w.document.head.innerHTML = '<style>.cardImageContainer { background-size: contain; background-position: center; }</style>';
    const css = w.document.createElement('style');
    css.textContent = fs.readFileSync(path.join(assets, 'tv-cinema.css'), 'utf8');
    w.document.head.appendChild(css);
    w.document.body.innerHTML = '<div class="card" data-type="TvChannel"><div class="cardImageContainer"></div></div>';
    assert.equal(w.getComputedStyle(w.document.querySelector('.cardImageContainer')).backgroundSize, 'contain');
});

test('search results containing channels do not become a Live TV page', async t => {
    const { w, clock, calls, load } = setup(t, { hash: '#/search' });
    w.document.body.innerHTML = '<div class="libraryPage"><h1>Suche</h1>' + '<div class="card" data-type="TvChannel"></div>'.repeat(4) + '</div>';
    load('tvLive.js');
    await clock.tickAsync(1000);
    assert.equal(calls.channels, 0);
    assert.equal(w.document.getElementById('firetv-live'), null);
});

test('empty filter cancels the remaining render chunks', async t => {
    const { w, clock, load } = setup(t, { items: 800 });
    load('tvLive.js');
    await clock.tickAsync(1);
    const input = w.document.querySelector('.firetv-live-search');
    input.value = 'no such channel';
    input.dispatchEvent(new w.Event('input', { bubbles: true }));
    await clock.tickAsync(1000);
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 0);
    assert.ok(w.document.querySelector('.firetv-live-empty'));
});

test('returning to Live TV mounts outside scrolling pages and preserves stock spinners', async t => {
    const { w, clock, load } = setup(t);
    const spinner = w.document.createElement('div');
    spinner.className = 'loading';
    w.document.body.appendChild(spinner);
    load('tvLive.js');
    await clock.tickAsync(500);
    w.location.hash = '#/movies';
    await clock.tickAsync(500);
    assert.equal(w.document.getElementById('firetv-live'), null);
    assert.equal(spinner.className, 'loading');
    assert.equal(spinner.style.display, '');
    w.document.querySelector('.libraryPage').classList.add('hide');
    const active = w.document.createElement('div');
    active.className = 'libraryPage';
    active.innerHTML = '<h1>Live TV</h1>';
    w.document.body.appendChild(active);
    w.location.hash = '#/livetv';
    await clock.tickAsync(500);
    assert.equal(w.document.getElementById('firetv-live').parentNode, w.document.body);
});

test('bootstrap removes competing viewport settings from hosted web HTML', async t => {
    const { w, clock, load } = setup(t);
    load('nativeshell.js');
    const stock = w.document.createElement('meta');
    stock.name = 'viewport';
    stock.content = 'width=device-width, initial-scale=1';
    w.document.head.appendChild(stock);
    await clock.tickAsync(1);
    w.FireTvGuard();
    const metas = w.document.querySelectorAll('meta[name="viewport"]');
    assert.equal(metas.length, 1);
    assert.equal(metas[0].content, 'width=1920, user-scalable=no, viewport-fit=cover');
});

test('EPG uses local time and refreshes visible programs without replacing focus', async t => {
    const { w, clock, load, channels } = setup(t);
    clock.setSystemTime(new Date('2026-07-01T12:00:00Z'));
    let name = 'First program';
    const get = w.ApiClient.getJSON;
    w.ApiClient.getJSON = url => url.startsWith('LiveTv/Programs') ? Promise.resolve({ Items: channels.map(c => ({
        ChannelId: c.Id, Name: name, StartDate: '2026-07-01T11:00:00Z', EndDate: '2026-07-01T13:00:00Z'
    })) }) : get(url);
    load('tvLive.js');
    await clock.tickAsync(1000);
    const row = w.document.querySelector('.firetv-live-row');
    row.focus();
    assert.ok(row.textContent.includes('First program'));
    const expected = new w.Date('2026-07-01T12:00:00Z');
    assert.equal(w.FireTvLive.clock('2026-07-01T12:00:00Z'), String(expected.getHours()).padStart(2, '0') + ':00');
    assert.equal(w.FireTvLive.clock('invalid'), '');
    name = 'Updated program';
    await clock.tickAsync(75000);
    assert.ok(row.textContent.includes('Updated program'));
    assert.equal(w.document.activeElement, row);
});

test('favorites groups recents and position survive navigation and stay account scoped', async t => {
    const { w, clock, load, channels } = setup(t, { items: 200 });
    channels[0].Tags = ['News']; channels[1].Tags = ['Sport'];
    let user = 'first'; w.ApiClient.getCurrentUserId = () => user;
    load('tvLive.js'); await clock.tickAsync(1000);
    w.document.querySelector('.firetv-live-favorite').click();
    w.document.querySelector('[data-mode="favorites"]').click();
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 1);
    w.FireTvLive.onPlaybackState({ event: 'playing', isLive: true, itemId: 'ch1' });
    w.document.querySelector('[data-mode="recent"]').click();
    assert.equal(w.document.querySelector('.firetv-live-row').getAttribute('data-id'), 'ch1');
    w.document.querySelector('[data-mode="all"]').click();
    const group = w.document.querySelector('.firetv-live-groups');
    group.value = 'News'; group.dispatchEvent(new w.Event('change'));
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 1);
    group.value = ''; group.dispatchEvent(new w.Event('change'));
    const viewport = w.document.querySelector('.firetv-live-viewport');
    viewport.scrollTop = 4000; viewport.dispatchEvent(new w.Event('scroll'));
    await clock.tickAsync(20);
    w.location.hash = '#/movies'; await clock.tickAsync(500);
    w.location.hash = '#/livetv'; await clock.tickAsync(500);
    assert.equal(w.document.querySelector('.firetv-live-viewport').scrollTop, 4000);
    user = 'second'; w.FireTvLive.sync(); await clock.tickAsync(500);
    w.document.querySelector('[data-mode="favorites"]').click();
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 0);
    user = 'first'; w.FireTvLive.sync(); await clock.tickAsync(500);
    w.document.querySelector('[data-mode="favorites"]').click();
    assert.equal(w.document.querySelectorAll('.firetv-live-row').length, 1);
});

test('channel refresh failure keeps loaded channels and announces retry', async t => {
    const { w, clock, load } = setup(t);
    load('tvLive.js'); await clock.tickAsync(1000);
    const row = w.document.querySelector('.firetv-live-row');
    const get = w.ApiClient.getJSON;
    w.ApiClient.getJSON = url => url.startsWith('LiveTv/Channels') ? Promise.reject(new Error('offline')) : get(url);
    await clock.tickAsync(5 * 60000 + 16000);
    assert.equal(w.document.querySelector('.firetv-live-row'), row);
    assert.match(w.document.querySelector('.firetv-live-status').textContent, /nicht erreichbar/);
});

test('playing a middle channel preserves adjacent queue order', async t => {
    const { w, clock, load, channels } = setup(t);
    let payload;
    w.NativePlayer = { loadPlayer: data => { payload = JSON.parse(data); } };
    load('tvLive.js'); await clock.tickAsync(1000);
    w.FireTvLive.play(channels[2], channels);
    assert.equal(payload.items[0].Id, 'ch2');
    assert.deepEqual(payload.ids, ['ch0', 'ch1', 'ch2', 'ch3']);
});

test('guide recordings and timers remain usable when tabs change without a new route', async t => {
    const {w, clock, load} = setup(t);
    const header = w.document.createElement('header');
    header.className = 'skinHeader';
    header.innerHTML = [0,1,2,3,4,5].map(i=>'<button class="emby-tab-button" data-index="'+i+'">Tab '+i+'</button>').join('');
    w.document.body.prepend(header);
    const buttons = header.querySelectorAll('button');
    buttons[0].classList.add('emby-tab-button-active');
    load('tvLive.js'); load('tvLive.js'); // same implementation served for the server's injected script
    await clock.tickAsync(600);
    assert.equal(w.document.querySelectorAll('#firetv-live').length, 1);
    for (const index of [1,3,4,5,2]) {
        buttons.forEach(button=>button.classList.remove('emby-tab-button-active'));
        buttons[index].classList.add('emby-tab-button-active');
        await clock.tickAsync(600);
        assert.equal(!!w.document.getElementById('firetv-live'), index===2);
    }
});

test('native playback pauses EPG polling and returns focus to the last watched channel', async t => {
    const {w, clock, load, calls} = setup(t, {items:100});
    load('tvLive.js'); await clock.tickAsync(1000);
    w.FireTvLive.setActive(false);
    const before = calls.guide;
    await clock.tickAsync(180000);
    assert.equal(calls.guide, before);
    w.FireTvLive.onPlaybackState({isLive:true, event:'playing', itemId:'ch60'});
    w.FireTvLive.setActive(true);
    assert.equal(w.document.activeElement.getAttribute('data-id'), 'ch60');
    await clock.tickAsync(1000);
    assert.ok(calls.guide>before);
});
