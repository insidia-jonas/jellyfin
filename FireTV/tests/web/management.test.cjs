const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const html = fs.readFileSync(path.resolve(__dirname, '../../../plugins/Jellyfin.Plugin.TreasureMaps/Configuration/management.html'), 'utf8');
const tick = () => new Promise(resolve => setTimeout(resolve, 10));
function open(reply) {
    const dom = new JSDOM(html, { url: 'http://jellyfin.test/web/', runScripts: 'dangerously', beforeParse(window) {
        window.ApiClient = { getUrl: route => route, ajax: options => Promise.resolve().then(() => reply(options.url.split('Management/')[1], options)) };
    }});
    const page = dom.window.document.getElementById('TreasureMapsManagementPage');
    page.dispatchEvent(new dom.window.Event('pageshow'));
    return { dom, page, document: dom.window.document };
}
test('management renders untrusted service/library text safely and maps loopback links to the server', async () => {
    const ui = open(() => ({ services: [{id:'radarr',online:true,url:'http://127.0.0.1:7878',version:'6.0'}], libraries:[{name:'<img src=x onerror=alert(1)>',paths:[]}],features:[],checkedAt:new Date() }));
    try {
        await tick();
        assert.equal(ui.document.querySelectorAll('#mcLibraryList img').length, 0);
        assert.equal(ui.document.querySelector('.mc-open').href, 'http://jellyfin.test:7878/');
        assert.match(ui.document.querySelector('#mcLibraryList').textContent, /<img/);
    } finally { ui.dom.window.close(); }
});
test('late status responses cannot update a hidden management page', async () => {
    let complete;
    const ui = open(() => new Promise(resolve => { complete = resolve; }));
    try {
        await tick(); ui.page.dispatchEvent(new ui.dom.window.Event('pagehide'));
        complete({services:[],libraries:[],features:[],version:'stale',checkedAt:new Date()}); await tick();
        assert.equal(ui.document.querySelector('#mcVersion').textContent, '');
    } finally { ui.dom.window.close(); }
});
test('management settings preserve existing keys and clear newly entered credentials on departure', async () => {
    const ui = open(route => route === 'Settings' ? {connections:[{service:'sabnzbd',url:'http://localhost:8080',keyPresent:true}],callbackUrl:'http://localhost:8096'} : {services:[],libraries:[],features:[],checkedAt:new Date()});
    try {
        ui.document.querySelector('[data-view=settings]').click(); await tick();
        const input=ui.document.querySelector('input[type=password]'); assert.equal(input.value, '');
        input.value='new-private-key'; ui.page.dispatchEvent(new ui.dom.window.Event('pagehide'));
        assert.equal(input.value,'');
    } finally { ui.dom.window.close(); }
});
