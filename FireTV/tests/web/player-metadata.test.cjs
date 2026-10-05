const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('native player receives series metadata for current and queued episodes', async () => {
    let delivered;
    const window = {
        ApiClient: {serverAddress: ()=>'http://jellyfin.test', accessToken: ()=>'token', getCurrentUserId: ()=>'user'},
        NativePlayer: {loadPlayer: value=>{delivered=JSON.parse(value)}},
        NativeInterface: {getDeviceInformation: ()=>JSON.stringify({deviceId:'test-tv',appVersion:'2.5.4'})}
    };
    const context = vm.createContext({window});
    const source = fs.readFileSync(path.resolve(__dirname,'../../app/src/main/assets/native/ExoPlayerPlugin.js'),'utf8');
    vm.runInContext(source.replace('export class ExoPlayerPlugin','class ExoPlayerPlugin') + '\nwindow.plugin = new ExoPlayerPlugin({});', context);
    const items = [5,6].map(n=>({Id:'episode-'+n,Name:'Folge '+n,Type:'Episode',SeriesName:'All Her Fault',SeriesId:'series',ParentIndexNumber:1,IndexNumber:n,Overview:'Deutsche Beschreibung.',RunTimeTicks:10000000}));
    await window.plugin.play({items,startPositionTicks:450000000});
    assert.deepEqual(delivered.ids,['episode-5','episode-6']);
    for (let n=0;n<2;n++) {
        for (const field of ['Name','SeriesName','SeriesId','ParentIndexNumber','IndexNumber','Overview','RunTimeTicks']) {
            assert.equal(delivered.items[n][field],items[n][field],field);
        }
    }
    assert.equal(delivered.startPositionTicks,450000000);
    assert.equal(delivered.deviceId,'test-tv');
});
