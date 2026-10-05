const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const FakeTimers = require('@sinonjs/fake-timers');

const scripts = {
    web: '../../../src/Jellyfin.LiveTv/Web/livetv-overview.js',
    firetv: '../../app/src/main/assets/native/tvLive.js'
};
const liveId = 'a'.repeat(32);
const movieId = 'b'.repeat(32);
const tvId = 'c'.repeat(32);
const list = id => '#/list?parentId=' + id;

for (const [client, script] of Object.entries(scripts)) {
    function setup(t, { pendingChannels = false, pendingParent = false } = {}) {
        const dom = new JSDOM('<html lang="de"><body><div class="libraryPage"><h1>Live TV</h1><div class="itemsContainer"></div></div></body></html>', {
            url: 'http://jellyfin.test/web/#/livetv', runScripts: 'outside-only', pretendToBeVisual: true
        });
        const w = dom.window;
        const observers = [];
        const Observer = w.MutationObserver;
        w.MutationObserver = class extends Observer {
            constructor(callback) { super(callback); observers.push(this); }
        };
        const clock = FakeTimers.withGlobal(w).install({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'requestAnimationFrame', 'cancelAnimationFrame', 'Date'] });
        const channels = [{ Id: 'channel1', Name: 'Sender', Type: 'TvChannel' }];
        let resolveChannels, resolveParent;
        const calls = { channels: 0, parents: 0 };
        w.ApiClient = {
            getCurrentUserId: () => 'user', getUrl: (p, params) => p + '?' + new URLSearchParams(params),
            getImageUrl: () => '',
            getJSON: url => {
                if (url.startsWith('LiveTv/Programs')) return Promise.resolve({ Items: [] });
                calls.channels++;
                return pendingChannels ? new Promise(r => { resolveChannels = r; }) : Promise.resolve({ Items: channels });
            },
            getItems: () => Promise.resolve({ Items: channels }),
            getItem: (user, id) => {
                calls.parents++;
                const item = id === liveId ? { Id: id, Name: 'Live TV', Type: 'Channel' }
                    : { Id: id, Name: id === movieId ? 'Filme' : 'Serien', Type: 'ChannelFolderItem', ProviderIds: { TreasureMapsCategory: id === movieId ? 'movies' : 'tv' } };
                return pendingParent && id === liveId ? new Promise(r => { resolveParent = () => r(item); }) : Promise.resolve(item);
            }
        };
        w.eval(fs.readFileSync(path.resolve(__dirname, script), 'utf8'));
        t.after(() => { observers.forEach(o => o.disconnect()); clock.uninstall(); w.close(); });
        const overlay = () => w.document.querySelector('#jf-livetv-overview, #firetv-live');
        const navigate = (route, method = 'pushState') => w.history[method]({}, '', route);
        const assertReleased = () => {
            assert.equal(overlay(), null);
            assert.equal(w.document.documentElement.matches('.jf-livetv-list-on, .firetv-live-on'), false);
        };
        return { w, clock, calls, overlay, navigate, assertReleased, resolveChannels: () => resolveChannels({ Items: channels }), resolveParent: () => resolveParent() };
    }

    if (client === 'firetv') {
        test('firetv: D-pad reaches programme info and returns to its channel without playback', async t => {
            const e = setup(t);
            await e.clock.tickAsync(800);
            const document = e.w.document;
            const key = (name, code) => document.activeElement.dispatchEvent(new e.w.KeyboardEvent('keydown', { key: name, keyCode: code, bubbles: true, cancelable: true }));
            document.querySelector('.firetv-live-row').focus();
            key('ArrowRight');
            assert.ok(document.activeElement.matches('.firetv-live-favorite'));
            key('Unidentified', 22);
            assert.ok(document.activeElement.matches('.firetv-live-info'));
            document.activeElement.click();
            assert.ok(document.querySelector('#firetv-guide'));
            e.w.FireTvLive.closeGuide();
            assert.ok(document.activeElement.matches('.firetv-live-info'));
            key('ArrowLeft'); key('ArrowLeft');
            assert.ok(document.activeElement.matches('.firetv-live-row'));
        });
    }

    for (const method of ['pushState', 'replaceState']) {
        test(client + ': ' + method + ' removes Live TV before rendering movies or series', async t => {
            const e = setup(t);
            await e.clock.tickAsync(800);
            assert.ok(e.overlay());
            for (const route of [list(movieId), list(tvId), '#/details?id=' + movieId, '#/movies', '#/tv', '#/search']) {
                e.navigate(route, method);
                e.assertReleased();
                // The old Live TV heading/cards remain mounted during navigation.
                e.w.document.querySelector('.itemsContainer').innerHTML = '<div class="card" data-type="TvChannel">Jetzt: Sender</div>'.repeat(4);
                await e.clock.tickAsync(1000);
                e.assertReleased();
                e.navigate('#/livetv', method);
                await e.clock.tickAsync(800);
                assert.ok(e.overlay(), 'returning to Live TV restores its list');
            }
        });
    }

    test(client + ': late channel response cannot take over a new list page', async t => {
        const e = setup(t, { pendingChannels: true });
        await e.clock.tickAsync(50);
        e.navigate(list(movieId));
        e.resolveChannels();
        await e.clock.tickAsync(2000);
        e.assertReleased();
    });

    test(client + ': late parent lookup cannot claim a movie category', async t => {
        const e = setup(t, { pendingParent: true });
        await e.clock.tickAsync(500);
        e.navigate(list(liveId));
        await e.clock.tickAsync(500);
        e.navigate(list(tvId));
        e.resolveParent();
        await e.clock.tickAsync(2000);
        e.assertReleased();
        const calls = e.calls.parents;
        for (let i = 0; i < 5; i++) {
            e.w.document.querySelector('h1').textContent = 'Serien ' + i;
            await e.clock.tickAsync(500);
        }
        assert.equal(e.calls.parents, calls, 'DOM updates reuse the parent decision');
    });

    test(client + ': Back and Forward restore only the matching page', async t => {
        const e = setup(t);
        await e.clock.tickAsync(500);
        e.navigate(list(movieId));
        await e.clock.tickAsync(500);
        e.assertReleased();
        e.w.history.back();
        await e.clock.tickAsync(1000);
        assert.ok(e.overlay());
        e.w.history.forward();
        await e.clock.tickAsync(1000);
        e.assertReleased();
    });
}
