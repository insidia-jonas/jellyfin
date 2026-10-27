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

test('subtitle tools are collapsed below metadata and load a JSON Grok quote only when opened', async t => {
    const e = setup(t); const calls = [];
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="infoWrapper"><div class="nameContainer"></div></div><div class="detailSection"></div>');
    e.api.getItem = () => Promise.resolve({ Id: id, Name: 'Movie', Type: 'Movie' });
    e.api.getCurrentUser = () => Promise.resolve({ Configuration: { SubtitleLanguagePreference: 'de' } });
    e.api.ajax = options => {
        calls.push(options);
        assert.equal(options.dataType, 'json');
        return Promise.resolve({ ok: true, aiEnabled: true, opensubtitles: [], quote: { ok: true, enabled: true, summary: 'ca. 0.16 USD', totalUsd: 0.164, whisperUsd: 0.1583, translationUsd: 0.0057, includesTranslation: true, whisperModel: 'grok-voice-transcribe-2.0' } });
    };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const panel = e.w.document.querySelector('#tmSubtitles');
    assert.equal(panel.tagName, 'DETAILS'); assert.equal(panel.open, false);
    assert.equal(panel.closest('.infoWrapper'), null); assert.ok(panel.closest('.detailSection'));
    assert.equal(calls.length, 0);
    panel.open = true; panel.dispatchEvent(new e.w.Event('toggle')); await e.clock.tickAsync(1);
    assert.equal(calls.filter(c => c.url.includes('Subtitles/Search')).length, 1);
    assert.equal(calls.filter(c => c.url.includes('Subtitles/Sync')).length, 1);
    assert.ok(calls.every(c => c.type === 'GET'));

    assert.match(panel.querySelector('.tmSubHint').textContent, /Grok 0.16 USD/);
    assert.equal(panel.querySelector('.tmSubGen').disabled, false);
});

test('a missing subtitle quote cannot advertise zero cost or start generation', async t => {
    const e = setup(t); let generated = 0;
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="detailSection"></div>');
    e.api.getItem = () => Promise.resolve({ Id: id, Name: 'Movie', Type: 'Movie' });
    e.api.getCurrentUser = () => Promise.resolve({});
    e.api.ajax = options => { if (options.type === 'POST') { generated++; } return Promise.resolve({ ok: true, aiEnabled: true, quote: null }); };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    const panel = e.w.document.querySelector('#tmSubtitles'); panel.open = true;
    panel.dispatchEvent(new e.w.Event('toggle')); await e.clock.tickAsync(1);
    const button = panel.querySelector('.tmSubGen'); assert.equal(button.disabled, true);
    assert.doesNotMatch(panel.textContent, /0.00 USD/); button.click(); assert.equal(generated, 0);
});

test('local alignment uses selected tracks and queues a free job without generating AI subtitles', async t => {
    const e=setup(t), calls=[];let accepted;
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin','<div class="detailSection"></div>');
    e.api.getItem=()=>Promise.resolve({Id:id,Name:'Resident Evil',Type:'Movie'});
    e.api.getCurrentUser=()=>Promise.resolve({});
    e.api.getUrl=(p,q)=>p+'?'+new URLSearchParams(q);
    e.api.ajax=o=>{calls.push(o);return Promise.resolve(o.type==='POST'?{ok:true,job:{id:'sync',kind:'sync'}}:
        o.url.includes('Subtitles/Sync')?{available:true,subtitles:[{index:6,title:'English ASS'}],audio:[{index:4,title:'Deutsch'},{index:5,title:'English'}]}:{ok:true,quote:null});};
    e.w.EvolutionSubtitleJobs={subscribe:()=>{},accept:j=>{accepted=j;}};
    e.w.confirm=()=>true;e.w.ApiClient=e.api;e.load();await e.clock.tickAsync(1200);
    const panel=e.w.document.querySelector('#tmSubtitles');panel.open=true;panel.dispatchEvent(new e.w.Event('toggle'));await e.clock.tickAsync(1);
    const audio=panel.querySelector('[aria-label="Tonspur für Abgleich"]');audio.value='5';
    Array.from(panel.querySelectorAll('button')).find(b=>b.textContent==='Mit Tonspur abgleichen').click();await e.clock.tickAsync(1);
    assert.equal(accepted.kind,'sync');assert.equal(calls.filter(c=>c.type==='POST').length,1);
    assert.match(calls.at(-1).url,/Subtitles\/Sync.*subtitleIndex=6.*audioIndex=5/);
    assert.ok(!calls.some(c=>c.url.includes('/Generate')));
});

test('confirmed generation hands an accepted job to the persistent observer instead of waiting for the film', async t => {
    const e=setup(t), calls=[];let accepted=null,subscription;
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin','<div class="detailSection"></div>');
    e.api.getItem=()=>Promise.resolve({Id:id,Name:'Movie',Type:'Movie'});
    e.api.getCurrentUser=()=>Promise.resolve({});
    e.api.getUrl=(p,q)=>p+'?'+new URLSearchParams(q);
    const job={id:'job',itemId:id,language:'de',state:'queued',active:true,percent:0,message:'Du kannst die Filmseite verlassen.'};
    e.api.ajax=o=>{calls.push(o);return Promise.resolve(o.type==='POST'?{ok:true,job}:{ok:true,quote:{ok:true,enabled:true,summary:'ca. 0.16 USD',totalUsd:.16}});};
    e.w.EvolutionSubtitleJobs={subscribe:(_,fn)=>{subscription=fn;fn([]);},accept:j=>{accepted=j;subscription([j]);}};
    e.w.confirm=()=>true;e.w.ApiClient=e.api;e.load();await e.clock.tickAsync(1200);
    const panel=e.w.document.querySelector('#tmSubtitles');panel.open=true;panel.dispatchEvent(new e.w.Event('toggle'));await e.clock.tickAsync(1);
    panel.querySelector('.tmSubGen').click();await e.clock.tickAsync(1);
    assert.equal(accepted,job);assert.match(calls.at(-1).url,/confirmed=true/);assert.match(calls.at(-1).url,/maxEstimatedUsd=0.16/);
    assert.equal(panel.querySelector('.tmSubGen').disabled,true);assert.match(panel.querySelector('.tmSubHint').textContent,/Film verlassen/);
    assert.doesNotMatch(panel.textContent,/Fenster offen lassen/);
});

test('category loading is visible, times out, retries and ignores a late response', async t => {
    const e = setup(t); let finish;
    e.w.history.replaceState({}, '', '#/list?parentId=' + id);
    e.w.document.querySelector('.page').innerHTML = '<div class="itemsContainer"></div>';
    e.api.getItem = () => Promise.resolve({ Id: id, Name: 'Trending', ChannelId: 'channel' });
    e.api.getItems = () => new Promise(r => { finish = r; });
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    assert.equal(e.w.document.querySelector('.tmLoadMessage').getAttribute('aria-busy'), 'true');
    await e.clock.tickAsync(17000);
    assert.match(e.w.document.querySelector('.tmLoadMessage').textContent, /nicht rechtzeitig/);
    const old = finish;
    e.api.getItems = () => Promise.resolve({ Items: [{ ProviderIds: { TreasureMapsCategory: 'movies' } }] });
    e.w.document.querySelector('.tmRetry').click(); await e.clock.tickAsync(1);
    assert.equal(e.w.document.querySelector('.tmLoadMessage'), null);
    old({ Items: [] }); await e.clock.tickAsync(1);
    assert.equal(e.w.document.querySelector('.tmLoadMessage'), null);
});

test('Play on an unavailable indexer title focuses the release chooser without playback or a download', async t => {
    const e = setup(t); let played = 0; const calls = [];
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="mainDetailButtons"><button class="btnPlay">Play</button></div>');
    e.w.HTMLElement.prototype.scrollIntoView = function () {};
    e.w.playbackManager = { play: () => { played++; } };
    e.api.fetch = options => { calls.push(options); return Promise.resolve(options.url.startsWith('TreasureMaps/Playback') ? { available: false } : { items: [] }); };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    e.w.document.querySelector('#tmIndexerAction').click(); await e.clock.tickAsync(1);
    assert.match(e.w.document.querySelector('.tmPlaybackStatus').textContent, /Version.*Download/);
    assert.equal(e.w.document.activeElement.tagName, 'SUMMARY');
    assert.equal(played, 0); assert.equal(calls.some(c => c.type === 'POST'), false);
});

test('Play uses only the verified local movie and preserves resume position', async t => {
    const e = setup(t); let played;
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="mainDetailButtons"></div>');
    const original = e.api.getItem;
    e.api.getItem = (user, itemId) => itemId === 'local' ? Promise.resolve({ Id: 'local', Type: 'Movie', UserData: { PlaybackPositionTicks: 1230000 } }) : original();
    e.api.fetch = options => Promise.resolve(options.url.startsWith('TreasureMaps/Playback') ? { available: true, itemId: 'local' } : { items: [] });
    e.w.playbackManager = { play: request => { played = request; } };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    e.w.document.querySelector('#tmIndexerAction').click(); await e.clock.tickAsync(1);
    assert.equal(played.items[0].Id, 'local'); assert.equal(played.startPositionTicks, 1230000);
});

test('release rows expose the real filename and submit the selected item identity', async t => {
    const e = setup(t); let posted;
    e.api.getItems = () => Promise.resolve({ Items: [{ Id: 'release', Name: '1080p', ProviderIds: { TreasureMaps: 'guid', EvolutionReleaseName: 'The.Other.Mother.2017.1080p.WEB-DL' } }] });
    e.api.getUrl = (p, params) => p + (params ? '?' + new URLSearchParams(params) : '');
    e.api.fetch = options => { if (options.type === 'POST') posted = options; return Promise.resolve({ ok: true, items: [] }); };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    assert.match(e.w.document.querySelector('.tmSceneName').textContent, /Other.Mother.2017/);
    e.w.document.querySelector('#tmReleases .tmDl').click(); await e.clock.tickAsync(1);
    assert.match(posted.url, /itemId=release/);
});

test('Fire TV Play uses its registered player manager for a verified local copy', async t => {
    const e = setup(t); const played = [], calls = [];
    e.w.document.querySelector('.page').insertAdjacentHTML('afterbegin', '<div class="mainDetailButtons"></div>');
    const original = e.api.getItem;
    e.api.getItem = (user, itemId) => itemId === 'local' ? Promise.resolve({ Id: 'local', Type: 'Movie', UserData: { PlaybackPositionTicks: 54000000 } }) : original();
    e.api.fetch = options => { calls.push(options); return Promise.resolve(options.url.startsWith('TreasureMaps/Playback') ? { available: true, itemId: 'local' } : { items: [] }); };
    // The Cube has neither window.playbackManager nor the desktop Live TV resolver.
    e.w.ExoPlayer = { playbackManager: { play: request => { played.push(request); } }, play: () => assert.fail('Do not bypass Jellyfin playback management') };
    e.w.ApiClient = e.api; e.load(); await e.clock.tickAsync(1200);
    e.w.document.querySelector('#tmIndexerAction').click(); await e.clock.tickAsync(1);
    assert.equal(played.length, 1); assert.equal(played[0].items[0].Id, 'local');
    assert.equal(played[0].startPositionTicks, 54000000);
    assert.equal(calls.some(c => c.type === 'POST'), false);
    assert.equal(e.w.location.hash, '#/details?id=' + id);
});
