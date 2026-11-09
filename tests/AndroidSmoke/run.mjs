// Run against an emulator and a Release build with DefineConstants=ZERA_TESTING.
// Uses only synthetic local files and a local WebDAV fixture.
import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
const adb=process.env.ADB??path.join(process.env.LOCALAPPDATA,'Android/Sdk/platform-tools/adb.exe');
const serial=process.env.ANDROID_SERIAL??'emulator-5554';
if(!serial.startsWith('emulator-'))throw new Error('Test scripts require an emulator');
const out=path.resolve('scratch/android-acceptance');fs.mkdirSync(out,{recursive:true});
const command=(...args)=>execFileSync(adb,['-s',serial,...args],{encoding:'utf8'}).trim();
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let socket,seq=0;const pending=new Map(),checks=[];
function check(ok,label){if(!ok)throw new Error(label);checks.push(label);console.log('PASS '+label);}
async function connect(){
  const pid=command('shell','pidof','com.zera.downloader');
  command('forward','tcp:9222','localabstract:webview_devtools_remote_'+pid);
  let pages=[];for(let i=0;i<50;i++){try{pages=await(await fetch('http://127.0.0.1:9222/json')).json();if(pages.length)break;}catch{}await delay(100);}
  socket=new WebSocket(pages[0].webSocketDebuggerUrl);await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject;});
  socket.onmessage=e=>{const result=JSON.parse(e.data);const request=pending.get(result.id);if(request){clearTimeout(request.timer);pending.delete(result.id);request.resolve(result);}};
  for(let i=0;i<100;i++){if(await evaluate(`document.readyState==='complete'&&typeof window.zeraRefresh==='function'`))break;await delay(100);}
  await evaluate(`window.__smokePending=new Map();window.__smokeCounter=0;window.__baseZeraResolve??=window.zeraResolve;window.zeraResolve=(id,res)=>{const callback=window.__smokePending.get(id);if(callback){window.__smokePending.delete(id);callback(res);}else window.__baseZeraResolve?.(id,res);};window.__smokeRpc=(action,payload)=>new Promise(resolve=>{const id='smoke_'+(++window.__smokeCounter);window.__smokePending.set(id,resolve);AndroidBridge.dispatch(JSON.stringify({id,action,payload}));setTimeout(()=>{if(window.__smokePending.delete(id))resolve({ok:false,error:'smoke timeout'});},20000);});true`);
}
function send(method,params={}){return new Promise((resolve,reject)=>{const id=++seq;const timer=setTimeout(()=>{pending.delete(id);reject(new Error('CDP timeout: '+method));},25000);pending.set(id,{resolve,timer});socket.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const reply=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(reply.result?.exceptionDetails)throw new Error(JSON.stringify(reply.result.exceptionDetails));return reply.result?.result?.value;}
async function rpc(action,payload={}){const reply=await evaluate(`window.__smokeRpc(${JSON.stringify(action)},${JSON.stringify(payload)})`);if(!reply?.ok)throw new Error(action+': '+reply?.error);return reply.data;}
async function finished(id){for(let i=0;i<120;i++){const s=await rpc('getState');const t=s.tasks.find(t=>t.id===id);if(t?.state==='Completed')return t;if(t?.state==='Failed')throw new Error(t.error);await delay(150);}throw new Error('Download did not complete');}
const hash='047eb196e8f681dc3967913345ff4af3371dce9d8bdd357515c12c5f110c8205';
async function screenshot(name){const r=await send('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(out,name+'.png'),Buffer.from(r.result.data,'base64'));}
try{
  command('reverse','tcp:18880','tcp:18880');
  await connect();
  let state=await rpc('getState');check(state.platforms.length===10&&!state.error,'ten native platforms and readable private state');
  check(await evaluate(`document.querySelectorAll('#platform-chips button').length`)===10,'UI renders actual native platform list');
  await evaluate(`document.querySelector('.app-nav [data-target="tab-settings"]').click();document.getElementById('setting-threads').value='16';true`);
  await delay(1800);
  check(await evaluate(`document.getElementById('setting-threads').value`)==='16','periodic state refresh preserves unsaved settings');
  await evaluate(`document.getElementById('btn-save-settings').click();true`);await delay(300);
  check((await rpc('getState')).settings.threads===16,'settings save button commits edited value');
  await rpc('platform.select',{platform:2});await delay(1800);
  check(await evaluate(`document.getElementById('login-tab-cookie').classList.contains('hidden')&&document.getElementById('login-tab-json').classList.contains('hidden')&&!document.getElementById('login-tab-webdav').classList.contains('hidden')`),'WebDAV UI exposes only supported login form');
  await rpc('platform.select',{platform:3});await delay(1800);
  check(await evaluate(`document.getElementById('login-tab-cookie').classList.contains('hidden')&&!document.getElementById('login-tab-json').classList.contains('hidden')`),'native cloud UI hides unsupported Cookie login');
  await rpc('platform.select',{platform:9});await delay(1800);
  check(await evaluate(`!document.getElementById('login-anonymous-notice').classList.contains('hidden')`),'Lanzou UI exposes anonymous sharing');
  if(process.env.ZERA_UI_ONLY){
    const completed=(await rpc('torrent.list')).find(t=>t.state==='Completed');
    if(completed){
      const listing=await rpc('torrent.files',{id:completed.id});
      check(listing.files.some(f=>f.name==='payload.bin'),'completed BT results are accessible for export');
      const file=listing.files.find(f=>f.name==='payload.bin');
      const read=await rpc('test.readTorrentFile',{id:completed.id,file:file.id});
      check(read.sha256===hash,'BT file export provider returns exact file bytes');
    }
    for(const theme of ['light','dark']){
      await rpc('settings.save',{threads:8,concurrent:2,writeMode:'SharedFile',theme});await delay(1700);
      check(await evaluate(`getComputedStyle(document.body).backgroundColor`) === (theme==='dark'?'rgb(11, 16, 30)':'rgb(240, 244, 250)'),theme+' theme renders expected background');
      for(const page of ['download','tasks','accounts','settings']){
        await evaluate(`document.querySelector('.app-nav [data-target="tab-${page}"]').click();true`);await delay(100);await screenshot('android-'+theme+'-'+page);
      }
    }
    command('shell','cmd','uimode','night','yes');
    await rpc('settings.save',{threads:8,concurrent:2,writeMode:'SharedFile',theme:'system'});await delay(1700);
    check(await evaluate(`getComputedStyle(document.body).backgroundColor`)==='rgb(11, 16, 30)','system theme follows operating system preference');
    command('shell','cmd','uimode','night','no');await delay(1800);
    check(await evaluate(`getComputedStyle(document.body).backgroundColor`)==='rgb(240, 244, 250)','system theme follows real operating-system light switch');
    await rpc('settings.save',{threads:8,concurrent:2,writeMode:'SharedFile',theme:'light'});
    fs.writeFileSync(path.join(out,'ui-results.json'),JSON.stringify({date:new Date().toISOString(),checks},null,2));
    console.log('ALL ANDROID UI CHECKS PASSED');socket.close();process.exit(0);
  }
  await rpc('settings.save',{threads:4,concurrent:2,writeMode:'SharedFile',theme:'light'});
  state=await rpc('direct.add',{url:'http://127.0.0.1:18880/payload.bin',name:'direct-fixture.bin'});
  let task=await finished(state.tasks.at(-1).id);
  check(command('shell','sha256sum',task.path).startsWith(hash),'direct multi-range download exact SHA-256');
  state=await rpc('account.webdav',{name:'Local WebDAV fixture',endpoint:'http://127.0.0.1:18880/dav/',username:'test',password:'secret',allowHttp:true});
  const account=state.accounts.find(a=>a.name==='Local WebDAV fixture');check(account?.platform===2,'WebDAV authentication and Keystore-backed account save');
  const directory=await rpc('browse',{id:''});check(directory.files.some(f=>f.name==='payload.bin'),'authenticated WebDAV directory listing');
  state=await rpc('download.add',{ids:[directory.files.find(f=>f.name==='payload.bin').id]});
  const davTask=state.tasks.at(-1);await rpc('platform.select',{platform:9});task=await finished(davTask.id);
  check(command('shell','sha256sum',task.path).startsWith(hash),'WebDAV authenticated ranges retain original account across platform switch');
  let trackers=await rpc('tracker.get');trackers.manualText='http://127.0.0.1:18881/announce';trackers.autoUpdate=false;await rpc('tracker.save',trackers);
  check((await rpc('tracker.get')).manualText.includes('18881'),'Tracker persistence through native bridge');
  check(Array.isArray(await rpc('torrent.list')),'native BT bridge returns task list without JNI crash');
  await rpc('settings.save',{threads:2,concurrent:1,writeMode:'SegmentedFiles',theme:'dark'});
  state=await rpc('direct.add',{url:'http://127.0.0.1:18880/payload.bin',name:'restart-fixture.bin'});const id=state.tasks.at(-1).id;
  await delay(220);await rpc('task.pause',{id});
  command('shell','am','force-stop','com.zera.downloader');socket.close();
  command('shell','am','start','-W','-n','com.zera.downloader/crc64815d0045ae1a360d.MainActivity');await connect();
  state=await rpc('getState');check(state.tasks.find(t=>t.id===id)?.state==='Paused','unfinished download remains paused after process restart');
  check(state.accounts.some(a=>a.id===account.id)&&state.settings.theme==='dark','encrypted account and settings survive restart');
  await rpc('task.resume',{id});task=await finished(id);
  check(command('shell','sha256sum',task.path).startsWith(hash),'segmented mode resumes after process restart with exact SHA-256');
  const encryption=await rpc('test.inspectState');
  check(encryption.encrypted&&!encryption.containsTestPassword&&!encryption.containsTestAccount,'private state is encrypted on disk');
  if(process.env.ZERA_BT_FIXTURE){
    command('reverse','tcp:18881','tcp:18881');command('reverse','tcp:18900','tcp:18900');
    const torrent=fs.readFileSync(process.env.ZERA_BT_FIXTURE).toString('base64');
    for(const split of [1,2,4,8]){
      const jobs=await rpc('test.addTorrent',{torrent,split});const id=jobs.at(-1).id;let completed;
      for(let n=0;n<400;n++){
        const job=(await rpc('torrent.list')).find(t=>t.id===id);
        if(job.state==='Failed')throw new Error(job.error);
        if(job.state==='Completed'){completed=job;break;}await delay(200);
      }
      check(!!completed,split===1?'native ordinary BT completes local swarm':'native '+split+'-session split BT completes and merges local swarm');
      const file=path.posix.join(completed.path,'payload.bin');
      check(command('shell','sha256sum',file).startsWith(hash),'BT '+split+'-session output exact SHA-256');
    }
    const privatePath=path.join(path.dirname(process.env.ZERA_BT_FIXTURE),'private.torrent');
    if(fs.existsSync(privatePath)){
      const jobs=await rpc('test.addTorrent',{torrent:fs.readFileSync(privatePath).toString('base64'),split:1});const id=jobs.at(-1).id;let completed;
      for(let n=0;n<300;n++){const job=(await rpc('torrent.list')).find(t=>t.id===id);if(job.state==='Failed')throw new Error(job.error);if(job.state==='Completed'){completed=job;break;}await delay(200);}
      check(!!completed&&command('shell','sha256sum',path.posix.join(completed.path,'payload.bin')).startsWith(hash),'private torrent retains embedded tracker and exact content');
      const index=JSON.parse(command('shell','cat',path.posix.join(path.dirname(completed.path),'..','jobs.json')));
      check(index.find(t=>t.id===id)?.trackers.length===0,'private torrent excludes configured public trackers');
    }
  }
  await evaluate('window.zeraRefresh();true');await delay(300);
  check(await evaluate(`getComputedStyle(document.body).backgroundColor`)==='rgb(11, 16, 30)','dark theme applies actual dark background');
  await screenshot('android-download-dark');
  check(await evaluate('document.documentElement.scrollWidth<=window.innerWidth'),'mobile viewport has no horizontal page overflow');
  fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({date:new Date().toISOString(),checks},null,2));
  if(fs.existsSync(path.join(out,'failure.txt')))fs.unlinkSync(path.join(out,'failure.txt'));
  console.log('ALL ANDROID SMOKE CHECKS PASSED');
}catch(error){fs.writeFileSync(path.join(out,'failure.txt'),error.stack);console.error(error);process.exitCode=1;}
finally{socket?.close();}
