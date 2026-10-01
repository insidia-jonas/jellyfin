const assert = require('node:assert/strict');
const {test} = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const {JSDOM} = require('jsdom');
const {IDBFactory} = require('fake-indexeddb');
const source = fs.readFileSync(path.resolve(__dirname, '../../app/src/main/assets/native/tvLogos.js'), 'utf8');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

function session(t, database = new IDBFactory()) {
    const dom = new JSDOM('<body></body>', {url: 'http://jellyfin.test', runScripts: 'outside-only'});
    const w = dom.window;
    const calls = [];
    const revoked = [];
    let number = 0;
    w.indexedDB = database;
    w.URL.createObjectURL = () => 'blob:session-' + Math.random() + '-' + (++number);
    w.URL.revokeObjectURL = url => revoked.push(url);
    w.fetch = async (url, options) => {
        calls.push({url, token: options.headers['X-Emby-Token']});
        return {ok:true, headers:{get:()=> 'image/png'}, blob:async()=>new Blob(['logo'])};
    };
    w.eval(source);
    t.after(()=>w.close());
    function mount(key, url = '/logo', token = 'test-token') {
        const img = w.document.createElement('img');
        w.document.body.appendChild(img);
        w.FireTvLogos.mount(img, key, url, token);
        return img;
    }
    return {w, calls, revoked, mount};
}

async function loaded(img) {
    for (let i = 0; i < 100 && !img.getAttribute('src'); i++) await delay(5);
    assert.ok(img.getAttribute('src'), 'cache job completed');
    return img.getAttribute('src');
}

test('cached blobs receive a new object URL after a WebView restart', async t => {
    const db = new IDBFactory();
    const first = session(t, db);
    const oldUrl = await loaded(first.mount('server|user|channel'));
    await delay(30); // commit the IndexedDB transaction
    const second = session(t, db);
    const newUrl = await loaded(second.mount('server|user|channel'));
    assert.equal(second.calls.length, 0, 'use the persistent blob without downloading again');
    assert.notEqual(newUrl, oldUrl, 'never reuse a URL belonging to a previous document');
});

test('cache deduplicates downloads and isolates server and user scopes', async t => {
    const s = session(t);
    const first = s.mount('server|one|channel');
    const duplicate = s.mount('server|one|channel');
    assert.equal(await loaded(first), await loaded(duplicate));
    assert.equal(s.calls.length, 1);
    await loaded(s.mount('server|two|channel', '/logo', 'other-token'));
    assert.equal(s.calls.length, 2);
    assert.equal(s.calls[1].token, 'other-token');
});

test('a failed refresh keeps the last successful logo', async t => {
    const s = session(t);
    const previous = await loaded(s.mount('channel', '/logo?tag=one'));
    s.w.fetch = async () => {throw new Error('offline');};
    assert.equal(await loaded(s.mount('channel', '/logo?tag=two')), previous);
});

test('only three logo fetches run concurrently and detached queued rows are skipped', async t => {
    const s = session(t);
    const releases = [];
    let active = 0, peak = 0, requests = 0;
    s.w.fetch = async () => {
        active++; requests++; peak = Math.max(peak, active);
        await new Promise(resolve => releases.push(resolve));
        active--;
        return {ok:true, headers:{get:()=> 'image/png'}, blob:async()=>new Blob(['logo'])};
    };
    const images = Array.from({length:12}, (_,i)=>s.mount('channel'+i));
    images.slice(3).forEach(img=>img.remove());
    for (let i = 0; i < 100 && releases.length < 3; i++) await delay(5);
    assert.equal(releases.length, 3);
    releases.forEach(resolve=>resolve());
    await Promise.all(images.slice(0,3).map(loaded));
    await delay(30);
    assert.equal(peak, 3);
    assert.equal(requests, 3);
});

test('memory cache stays bounded and revokes evicted blob URLs', async t => {
    const s = session(t);
    for (let i = 0; i < 36; i++) await loaded(s.mount('channel'+i));
    assert.equal(s.revoked.length, 4);
});
