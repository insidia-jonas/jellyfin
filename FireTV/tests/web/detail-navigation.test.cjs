const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {JSDOM} = require('jsdom');

function setup(t) {
    const dom = new JSDOM('<html class="layout-tv"><body><button id="play">Play</button><div tabindex="0" id="plot">Long description</div><div id="tmReleases"><details open><summary id="language">Deutsch</summary><button id="download">Download</button></details></div><details id="tmSubtitles"><summary id="subtitles">Untertitel</summary><button id="generate">Generieren</button></details><button disabled id="disabled">Unavailable</button><button hidden id="hidden">Hidden</button></body></html>', {url:'http://jellyfin.test/web/#/details?id=movie',runScripts:'outside-only',pretendToBeVisual:true});
    const w = dom.window;
    Array.from(w.document.querySelectorAll('[id]')).forEach((e,i) => {
        e.getBoundingClientRect = () => ({left:100,right:400,top:i*100,bottom:i*100+50,width:300,height:50});
        e.scrollIntoView = () => { e.dataset.scrolled = 'true'; };
    });
    w.eval(fs.readFileSync(path.resolve(__dirname,'../../app/src/main/assets/native/tvNavigation.js'),'utf8'));
    t.after(() => w.close());
    return {w, el:id=>w.document.getElementById(id), key:(key,opts={})=>w.document.activeElement.dispatchEvent(new w.KeyboardEvent('keydown',{key,bubbles:true,cancelable:true,...opts}))};
}
test('D-pad skips selectable plot and reaches language, download and subtitle actions', t => {
    const {w,el,key} = setup(t); el('play').focus();
    const selection = w.getSelection(), range = w.document.createRange(); range.selectNodeContents(el('plot')); selection.addRange(range);
    key('ArrowDown'); assert.equal(w.document.activeElement,el('language')); assert.equal(selection.rangeCount,0);
    key('ArrowDown'); assert.equal(w.document.activeElement,el('download'));
    key('ArrowDown'); assert.equal(w.document.activeElement,el('subtitles')); assert.equal(el('subtitles').dataset.scrolled,'true');
    key('ArrowDown'); assert.equal(w.document.activeElement,el('subtitles'),'closed/disabled/hidden controls cannot steal focus');
    key('Enter'); assert.equal(el('tmSubtitles').open,true);
    key('ArrowDown'); assert.equal(w.document.activeElement,el('generate'));
    key('ArrowUp'); assert.equal(w.document.activeElement,el('subtitles'));
});
test('Enter activates download once and never bills/generates while merely navigating', t => {
    const {w,el,key} = setup(t); let downloads=0, generates=0;
    el('download').onclick = () => downloads++; el('generate').onclick=()=>generates++;
    el('download').focus(); key('Enter'); key('Enter',{repeat:true});
    assert.equal(downloads,1); assert.equal(generates,0);
});
test('closed subtitle language selector can be left with D-pad while Enter retains its native picker', t => {
    const {w,el,key}=setup(t);const select=w.document.createElement('select');select.innerHTML='<option>Deutsch</option><option>English</option>';
    w.document.body.appendChild(select);select.getBoundingClientRect=()=>({left:0,right:80,top:400,bottom:450,width:80,height:50});select.scrollIntoView=()=>{};
    el('download').getBoundingClientRect=()=>({left:100,right:400,top:400,bottom:450,width:300,height:50});
    select.focus();assert.equal(key('Enter'),true);key('ArrowRight');assert.equal(w.document.activeElement,el('download'));assert.equal(select.value,'Deutsch');
});
test('Detail navigation respects editable inputs, modal boundaries and other routes', t => {
    const {w,el,key} = setup(t); const input=w.document.createElement('input'); w.document.body.appendChild(input); input.focus();
    assert.equal(key('ArrowDown'),true);
    const dialog=w.document.createElement('div'); dialog.setAttribute('role','dialog'); dialog.appendChild(el('download')); dialog.appendChild(el('play')); dialog.getBoundingClientRect=()=>({width:500,height:500}); w.document.body.appendChild(dialog);
    el('download').focus(); key('ArrowDown'); assert.equal(w.document.activeElement,el('download'));
    w.history.pushState({},'','#/livetv'); assert.equal(key('ArrowDown'),true);
});
