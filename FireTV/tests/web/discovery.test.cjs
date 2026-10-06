const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');
const {JSDOM}=require('jsdom');const FakeTimers=require('@sinonjs/fake-timers');
const source=fs.readFileSync(path.resolve(__dirname,'../../../plugins/Jellyfin.Plugin.TreasureMaps/Web/discovery.js'),'utf8');
function setup(t,hash='#/details?id=movie'){
 const dom=new JSDOM('<div id="itemDetailPage" class="tmTitlePage tmChannelPage"><div class="detailPagePrimaryContent"></div><div id="tmReleases"><details><summary>Deutsch</summary><button id="download">Download</button></details></div></div>',{url:'http://jf.test/web/'+hash,runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,clock=FakeTimers.withGlobal(w).install(),calls=[];let response=()=>Promise.resolve({ok:true,status:200,json:async()=>({available:true,rating:8.1,ratingSource:'IMDb',overview:'Eine deutsche Beschreibung.',year:2025})});
 w.ApiClient={getCurrentUserId:()=> 'u',accessToken:()=> 'secret',serverAddress:()=> 'http://jf.test',getUrl:p=>'http://jf.test/'+p,getItem:async(u,id)=>({Id:id,ProviderIds:{TreasureMapsKind:'movie'}})};
 w.fetch=(url,options)=>{calls.push({url,options});return response(url,options)};
 w.HTMLElement.prototype.scrollIntoView=function(){};
 t.after(()=>{clock.uninstall();w.close()});return{w,clock,calls,load:()=>w.eval(source),respond:f=>response=f};
}
test('metadata is loaded separately with explicit rating provenance and action navigation never downloads',async t=>{
 const e=setup(t);let grabs=0;e.w.document.getElementById('download').onclick=()=>grabs++;e.load();await e.clock.tickAsync(10);
 assert.match(e.w.document.querySelector('.evolution-metadata').textContent,/IMDb.*8.1.*deutsche Beschreibung/);
 e.w.document.querySelector('.evolution-detail-actions button').click();assert.equal(e.w.document.activeElement.tagName,'SUMMARY');assert.equal(grabs,0);
 assert.equal(e.calls.length,1);assert.match(e.calls[0].url,/Metadata\/movie$/);
});
test('late metadata cannot update a reused page or another account',async t=>{
 const e=setup(t);let complete;e.respond(()=>new Promise(r=>complete=r));e.load();await e.clock.tickAsync(10);
 e.w.history.pushState({},'','#/home');complete({ok:true,status:200,json:async()=>({available:true,overview:'OLD SECRET'})});await e.clock.tickAsync(10);
 assert.equal(e.w.document.querySelector('.evolution-metadata'),null);assert.doesNotMatch(e.w.document.body.textContent,/OLD SECRET/);
});
test('visible title warmup is limited to 24 identities and does not repeat during polling',async t=>{
 const e=setup(t,'#/list?parentId=category');const host=e.w.document.getElementById('itemDetailPage');
 for(let i=0;i<60;i++){const card=e.w.document.createElement('button');card.className='card';card.dataset.type='BoxSet';card.dataset.id=i.toString(16).padStart(32,'0');card.getBoundingClientRect=()=>({width:100,top:i<30?100:3000,bottom:i<30?200:3100});host.appendChild(card);}
 e.load();await e.clock.tickAsync(10);assert.equal(JSON.parse(e.calls[0].options.body).length,24);
 await e.clock.tickAsync(2500);assert.equal(e.calls.length,2);assert.equal(JSON.parse(e.calls[1].options.body).length,6);
 assert.ok(e.calls.every(c=>/Metadata\/Warm$/.test(c.url)));
});
test('all four home identities replace imagery including a text-only Live TV card',async t=>{
 const e=setup(t,'#/home');e.w.document.body.innerHTML='<div id="homeTab"><div class="section0">'+['Filme','Serien','Indexer','Live TV'].map((name,i)=>'<button class="card" '+(i<2?'data-collectiontype="'+['movies','tvshows'][i]+'"':'')+'><div class="cardScalable"><span>'+name+'</span></div><div class="cardText-first"><bdi>'+name+'</bdi></div></button>').join('')+'</div></div>';
 e.load();await e.clock.tickAsync(10);assert.equal(e.w.document.querySelectorAll('.evolution-hub-art').length,4);assert.equal(e.calls.length,0);
});

test('library artwork retains native link delegation and restores cached cards across navigation',async t=>{
 const e=setup(t,'#/home'),d=e.w.document;
 const style=d.createElement('style');style.textContent=fs.readFileSync(path.resolve(__dirname,'../../../plugins/Jellyfin.Plugin.TreasureMaps/Web/cinema.css'),'utf8');d.head.appendChild(style);
 d.documentElement.className='cinema-ui cinema-home';
 d.body.innerHTML='<div id="homeTab"><div class="section0"><div class="card" data-collectiontype="tvshows"><div class="cardScalable"><div class="cardPadder"><span>Original icon</span></div><a class="cardImageContainer itemAction" data-action="link" href="#/tv?topParentId=series" aria-label="TV Shows"></a></div><div class="cardText-first"><bdi>TV Shows</bdi></div></div></div></div>';
 const card=d.querySelector('.card'),link=card.querySelector('a');let clicks=0;
 d.getElementById('homeTab').addEventListener('click',event=>{if(event.target.closest('.itemAction')===link){clicks++;}event.preventDefault();});
 e.load();await e.clock.tickAsync(10);
 for(let cycle=0;cycle<3;cycle++){
  const art=card.querySelector('.evolution-hub-art');assert.ok(art);
  assert.equal(e.w.getComputedStyle(link).visibility,'visible','native anchor must remain keyboard- and pointer-accessible');
  assert.equal(link.getAttribute('aria-label'),'Serien');
  art.querySelector('svg').dispatchEvent(new e.w.MouseEvent('click',{bubbles:true,cancelable:true}));
  assert.equal(clicks,cycle+1,'decoration must participate in the original delegated link action');
  e.w.history.pushState({},'','#/details?id=series');
  assert.equal(card.querySelector('.evolution-hub-art'),null);
  assert.equal(card.classList.contains('evolution-hub'),false);
  assert.equal(card.hasAttribute('data-hub'),false);
  assert.equal(card.hasAttribute('aria-label'),false);
  assert.equal(link.getAttribute('aria-label'),'TV Shows');
  assert.equal(card.querySelector('bdi').textContent,'TV Shows');
  e.w.history.pushState({},'','#/home');await e.clock.tickAsync(10);
  assert.equal(card.querySelectorAll('.evolution-hub-art').length,1);
  assert.equal(card.querySelector('a'),link,'cached native link and handlers are retained');
 }
});
