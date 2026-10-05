const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const FakeTimers=require('@sinonjs/fake-timers');
const script=fs.readFileSync(path.resolve(__dirname,'../../../plugins/Jellyfin.Plugin.TreasureMaps/Web/subtitle-jobs.js'),'utf8');
function setup(t){
 const dom=new JSDOM('<body><div class="headerRight"></div><div id="detail"></div></body>',{url:'http://test/web/#/details?id=film',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,clock=FakeTimers.withGlobal(w).install();let user='user',jobs=[],pending=null;
 const calls=[];
 w.ApiClient={getCurrentUserId:()=>user,serverId:()=> 'server',getUrl:(p,q)=>p+'?'+new URLSearchParams(q),ajax:o=>{calls.push(o);return pending?pending:Promise.resolve({jobs});}};
 w.confirm=()=>false;w.eval(script);
 t.after(()=>{clock.uninstall();w.close();});
 return {w,clock,calls,setUser:v=>user=v,setJobs:v=>jobs=v,setPending:v=>pending=v};
}
const job={id:'one',itemId:'film',title:'Film <script>',language:'de',state:'running',percent:20,active:true,message:'Sprache erkennen'};
test('jobs keep updating after leaving the film and restoring its detail view without generation',async t=>{
 const e=setup(t);e.setJobs([job]);let updates=[];
 e.w.EvolutionSubtitleJobs.subscribe(e.w.document.getElementById('detail'),j=>updates.push(j));
 await e.clock.tickAsync(1100);assert.equal(updates.at(-1)[0].percent,20);
 e.w.document.getElementById('detail').remove();e.w.history.pushState({},'','#/home');
 e.setJobs([{...job,percent:60}]);await e.clock.tickAsync(3000);
 const fresh=e.w.document.createElement('div');e.w.document.body.appendChild(fresh);
 e.w.EvolutionSubtitleJobs.subscribe(fresh,j=>updates.push(j));assert.equal(updates.at(-1)[0].percent,60);
 e.w.EvolutionSubtitleJobs.open();assert.match(e.w.document.getElementById('evolution-subtitle-jobs').textContent,/Film <script>/);
 assert.equal(e.w.document.querySelectorAll('script').length,0);
 assert.ok(e.calls.every(o=>o.type==='GET'),'navigation never starts paid work');
});
test('polling preserves button focus and Back closes job overview before leaving the route',async t=>{
 const e=setup(t);e.setJobs([job]);await e.clock.tickAsync(1100);e.w.EvolutionSubtitleJobs.open();await e.clock.tickAsync(1);
 const button=e.w.document.querySelector('.evolution-job-cancel');button.focus();
 e.setJobs([{...job,percent:55}]);await e.clock.tickAsync(3100);assert.equal(e.w.document.activeElement,button);
 const route=e.w.location.hash;e.w.dispatchEvent(new e.w.KeyboardEvent('keydown',{key:'Escape',cancelable:true}));
 assert.equal(e.w.document.getElementById('evolution-subtitle-jobs'),null);assert.equal(e.w.location.hash,route);
});
test('an old account response cannot expose jobs after logout',async t=>{
 const e=setup(t);let resolve;e.setPending(new Promise(r=>resolve=r));await e.clock.tickAsync(1100);
 e.setUser('');resolve({jobs:[job]});await e.clock.tickAsync(21000);
 assert.equal(e.w.document.querySelector('.evolution-jobs-trigger'),null);
});
