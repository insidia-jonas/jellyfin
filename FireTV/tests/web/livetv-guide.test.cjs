const assert=require('node:assert/strict');const {test}=require('node:test');const fs=require('node:fs');const path=require('node:path');
const {JSDOM}=require('jsdom');const FakeTimers=require('@sinonjs/fake-timers');
function setup(t,{count=1,pending=false,empty=false}={}) {
 const dom=new JSDOM('<html lang="de"><body><div class="libraryPage"><h1>Live TV</h1></div></body></html>',{url:'http://jellyfin.test/web/#/livetv',runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window;
 const clock=FakeTimers.withGlobal(w).install({now:Date.now(),toFake:['setTimeout','clearTimeout','setInterval','clearInterval','Date']});
 const observers=[],MO=w.MutationObserver;w.MutationObserver=class extends MO{constructor(cb){super(cb);observers.push(this)}};
 const calls=[],channels=Array.from({length:count},(_,i)=>({Id:'virtual-'+i,Type:'ChannelVideoItem',Name:'Sender '+i,ProviderIds:{LiveTvGuideChannel:'guide-'+i},UserData:{IsFavorite:i===0}}));
 let resolve;
 w.HTMLElement.prototype.scrollIntoView=function(){};
 w.ApiClient={getCurrentUserId:()=> 'user',getUrl:(p,a)=>p+'?'+new URLSearchParams(a),getImageUrl:()=>'',getJSON:url=>{
  if(!url.startsWith('LiveTv/Programs'))return Promise.resolve({Items:channels});
  const request=new URL(url,'http://jellyfin.test');calls.push(request);
  if(pending)return new Promise(r=>{resolve=r});
  return Promise.resolve({Items:empty?[]:request.searchParams.get('channelIds').split(',').map(id=>({ChannelId:id,Name:'Jetzt <img src=x onerror=alert(1)>',Overview:'Vollständige Handlung mit Hintergrundinformationen.',StartDate:new w.Date(w.Date.now()-60000).toISOString(),EndDate:new w.Date(w.Date.now()+60000).toISOString()}))});
 }};
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../../src/Jellyfin.LiveTv/Web/livetv-overview.js'),'utf8'));
 t.after(()=>{observers.forEach(o=>o.disconnect());clock.uninstall();w.close()});
 return{w,clock,calls,channels,resolve:()=>resolve({Items:[]})};
}
test('browser cards hydrate only the visible page with native guide IDs and preserve focus',async t=>{
 const e=setup(t,{count:120});await e.clock.tickAsync(1000);
 assert.equal(e.w.document.querySelectorAll('.jf-livetv-card').length,48);assert.equal(e.calls.length,4);
 assert.ok(e.calls.every(c=>c.searchParams.get('channelIds').split(',').length<=12));
 assert.ok(e.calls.every(c=>!c.searchParams.get('channelIds').includes('virtual')));
 const button=e.w.document.querySelector('.jf-live-info:not(.jf-live-play-label)');button.focus();
 await e.clock.tickAsync(15000);assert.equal(e.w.document.activeElement,button);
 assert.ok(e.w.document.querySelector('.jf-livetv-plot').textContent.includes('Vollständige Handlung'));
 assert.equal(e.w.document.querySelectorAll('img[src="x"]').length,0);
});
test('programme drawer shows real full descriptions, day controls and restores focus without playback',async t=>{
 const e=setup(t);await e.clock.tickAsync(500);
 const button=e.w.document.querySelector('.jf-live-info:not(.jf-live-play-label)');button.click();await e.clock.tickAsync(30);
 const dialog=e.w.document.querySelector('[role="dialog"]');assert.ok(dialog);assert.match(dialog.textContent,/Vollständige Handlung/);
 assert.equal(e.calls.at(-1).searchParams.get('channelIds'),'guide-0');assert.equal(e.calls.at(-1).searchParams.get('limit'),'150');
 dialog.dispatchEvent(new e.w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(e.w.document.querySelector('#jf-livetv-guide'),null);assert.equal(e.w.document.activeElement,button);
});
test('missing EPG, timeout and route departure leave usable channel navigation',async t=>{
 const e=setup(t,{pending:true});await e.clock.tickAsync(500);
 e.w.document.querySelector('.jf-live-info:not(.jf-live-play-label)').click();await e.clock.tickAsync(12500);
 assert.match(e.w.document.querySelector('#jf-livetv-guide').textContent,/erneut|Zeit|antwortet/i);
 e.w.history.pushState({},'','#/movies');e.resolve();await e.clock.tickAsync(1000);
 assert.equal(e.w.document.querySelector('#jf-livetv-guide'),null);assert.equal(e.w.document.documentElement.classList.contains('jf-guide-open'),false);
});
