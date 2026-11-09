// Run against an emulator and a Release build with DefineConstants=ZERA_TESTING.
// Uses only synthetic local files and a local WebDAV fixture.
import {execFileSync} from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
const adb=process.env.ADB??path.join(process.env.LOCALAPPDATA,'Android/Sdk/platform-tools/adb.exe');
const serial=process.env.ANDROID_SERIAL??'emulator-5554';
if(!serial.startsWith('emulator-'))throw new Error('Test scripts require an emulator');
const out=path.resolve('scratch/android-round2-acceptance');fs.mkdirSync(out,{recursive:true});
const command=(...args)=>execFileSync(adb,['-s',serial,...args],{encoding:'utf8'}).trim();
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let socket,seq=0;const pending=new Map(),checks=[];
function check(ok,label){if(!ok)throw new Error(label);checks.push(label);console.log('PASS '+label);}
async function connect(){
  let pid='';for(let i=0;i<50;i++){try{pid=command('shell','pidof','com.zera.downloader');if(pid)break;}catch{}await delay(200);}
  if(!pid)throw new Error('Application did not start');
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
async function rejected(action,payload,label){let failed=false;try{await rpc(action,payload);}catch{failed=true;}check(failed,label);}
async function click(selector){await evaluate(`document.querySelector(${JSON.stringify(selector)}).click();true`);}
async function textButton(text){await evaluate(`[...document.querySelectorAll('#modal-content button')].find(b=>b.textContent===${JSON.stringify(text)}).click();true`);}
async function refresh(){await evaluate('window.zeraRefresh();true');await delay(1800);}
try{
 await connect();
 await click('.app-nav [data-target="tab-settings"]');await delay(100);
 check(await evaluate(`document.querySelector('#setting-threads').getBoundingClientRect().width < document.querySelector('#setting-threads').parentElement.getBoundingClientRect().width*.7 && document.querySelector('#setting-threads').previousElementSibling.getBoundingClientRect().width>100`),'settings labels retain readable width');
 await screenshot('settings-layout');
 await rpc('test.cloudSetup',{share:false});await refresh();
 await click('.app-nav [data-target="tab-download"]');await click('#mode-drive');await delay(500);
 await rpc('test.cloudSetup',{share:false});await refresh();
 check(await evaluate(`!document.getElementById('btn-cloud-delete').classList.contains('hidden')&&document.getElementById('btn-cloud-delete').disabled`),'cloud delete follows native drive capability');
 await click('#file-list input[type="checkbox"]');
 check(await evaluate(`!document.getElementById('btn-cloud-delete').disabled`),'selection immediately enables cloud action');
 await click('#btn-cloud-delete');
 check(await evaluate(`document.getElementById('modal-content').innerText.includes('synthetic.txt')&&!document.querySelector('#modal-content input[type=checkbox]').checked`),'delete dialog names target and defaults to recoverable deletion');
 await textButton('确认删除');await delay(900);
 const calls=await rpc('test.cloudCalls');check(calls.some(x=>x.includes('/file/delete'))&&!calls.some(x=>x.includes('/recycle/remove')),'UI delete reaches provider without permanent purge');
 await rpc('test.cloudSetup',{share:true});await refresh();await click('#mode-share');
 await click('#file-list input[type="checkbox"]');await click('#btn-cloud-transfer');
 check(await evaluate(`document.getElementById('modal-content').innerText.includes('synthetic.txt')`),'transfer dialog previews selected names');
 await textButton('确认转存');await delay(1000);
 check((await rpc('test.cloudCalls')).some(x=>x.includes('/share/sharepage/save')),'transfer button invokes actual native operation');
 await click('#btn-cloud-mkdir');await evaluate(`document.querySelector('#modal-content input').value='/UI-folder';true`);await textButton('确认创建');await delay(700);
 check((await rpc('test.cloudCalls')).some(x=>x.includes('/UI-folder')),'folder dialog uses entered cloud path');
 await click('.app-nav [data-target="tab-accounts"]');await click('#btn-account-capacity');await delay(600);
 check(await evaluate(`document.getElementById('modal-content').innerText.includes('KB')`),'capacity button displays actual provider values');await click('#btn-modal-close');
 await rpc('tracker.save',{manualText:'http://127.0.0.1:18881/announce',autoUpdate:false,subscriptions:[{name:'Before edit',url:'https://example.test/old.txt',enabled:false,trackers:['http://127.0.0.1:18881/announce']}]});
 await click('.app-nav [data-target="tab-download"]');await click('#btn-open-trackers');await delay(500);
 await textButton('编辑');await evaluate(`(()=>{const f=document.querySelector('.tracker-edit-form');f.querySelector('input[type=text]').value='After edit';f.querySelector('input[type=url]').value='https://example.test/new.txt';return true;})()`);
 await textButton('保存修改');await textButton('保存全部配置');await delay(500);
 const tracker=await rpc('tracker.get');check(tracker.subscriptions[0].name==='After edit'&&tracker.subscriptions[0].url.endsWith('new.txt')&&!tracker.subscriptions[0].enabled&&tracker.subscriptions[0].trackers.length===1,'Tracker edit preserves disabled state and cache');
 await click('.app-nav [data-target="tab-tasks"]');await refresh();
 check(await evaluate(`!!document.querySelector('.workers-section:not(.hidden)')`),'split tasks expose child details');
 await click('.workers-section:not(.hidden) .workers-header');
 await evaluate(`window.__expandedList=document.querySelector('.workers-section:not(.hidden) .workers-list:not(.hidden)');window.__taskAction=document.querySelector('.task-actions-row button');window.__taskAction.focus();true`);
 await delay(1900);
 check(await evaluate(`window.__expandedList?.isConnected&&!window.__expandedList.classList.contains('hidden')&&document.activeElement===window.__taskAction`),'polling preserves worker expansion and task button focus');
 await screenshot('tasks-details');
 await click('.app-nav [data-target="tab-download"]');await click('#btn-import-qr-image');
 for(let i=0;i<30;i++){if(/topResumedActivity=.*documentsui/.test(command('shell','dumpsys','activity','activities')))break;await delay(200);}
 let returned=false;
 for(let i=0;i<8;i++){
  if(/topResumedActivity=.*com\.zera\.downloader/.test(command('shell','dumpsys','activity','activities'))){returned=true;break;}
  command('shell','input','keyevent','4');await delay(300);
 }
 check(returned,'image picker cancellation returns to native app foreground');
 check((await rpc('getState')).version==='3.0.0-preview.2','cancelled image picker leaves bridge usable');
 await send('Emulation.setDeviceMetricsOverride',{width:360,height:780,deviceScaleFactor:1,mobile:true});
 await click('.app-nav [data-target="tab-settings"]');await evaluate(`document.getElementById('setting-auto-export').scrollIntoView({block:'center'});true`);await delay(200);
 check(await evaluate(`document.documentElement.scrollWidth<=innerWidth&&document.querySelector('#setting-threads').previousElementSibling.getBoundingClientRect().width>100`),'360px settings keep readable labels and no page overflow');
 command('shell','screencap','-p','/sdcard/zera-settings-360.png');
 command('pull','/sdcard/zera-settings-360.png',path.join(out,'public-settings-360px.png'));
 await send('Emulation.clearDeviceMetricsOverride');
 fs.writeFileSync(path.join(out,'ui-results.json'),JSON.stringify({date:new Date().toISOString(),checks},null,2));
 if(fs.existsSync(path.join(out,'ui-failure.txt')))fs.unlinkSync(path.join(out,'ui-failure.txt'));
 console.log('ALL ROUND 2 UI CHECKS PASSED');
}catch(e){console.error(e);fs.writeFileSync(path.join(out,'ui-failure.txt'),e.stack);process.exitCode=1;}finally{socket?.close();}
