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
function exportedPath(uri,name){const id=decodeURIComponent(uri.split('/document/')[1]);if(!id?.startsWith('primary:Documents/ZERA-Test/'))throw new Error('Expected isolated ZERA-Test directory');return '/sdcard/'+id.slice(8)+'/'+name;}
async function notificationAction(label){
 command('shell','cmd','statusbar','expand-notifications');await delay(300);
 for(let i=0;i<3;i++){
  command('shell','uiautomator','dump','/sdcard/zera-notification.xml');
  const xml=command('shell','cat','/sdcard/zera-notification.xml');
  const node=xml.match(new RegExp('<node[^>]*text="'+label+'"[^>]*>'))?.[0];
  if(node){const b=node.match(/bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"/);command('shell','input','tap',String((+b[1]+ +b[3])/2),String((+b[2]+ +b[4])/2));command('shell','cmd','statusbar','collapse');return;}
  const expand=xml.match(/<node[^>]*content-desc="Expand"[^>]*>/)?.[0];
  if(expand){const b=expand.match(/bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"/);command('shell','input','tap',String((+b[1]+ +b[3])/2),String((+b[2]+ +b[4])/2));}else command('shell','input','swipe','500','650','500','1200','400');
  await delay(300);
 }
 throw new Error('Notification action not found: '+label);
}
try{
 command('reverse','tcp:18880','tcp:18880');await connect();
 let state=await rpc('getState');check(state.exportFolder?.name==='ZERA-Test','real SAF directory grant is retained');
 const tree=state.exportFolder.uri;
 let task=state.tasks.find(t=>t.state==='Completed');
 if(!task){state=await rpc('direct.add',{url:'http://127.0.0.1:18880/payload.bin',name:'export-fixture.bin'});task=await finished(state.tasks.at(-1).id);}
 const result=await rpc('system.exportTask',{id:task.id});
 const publicFile=exportedPath(result.uri,task.name);
 check(result.files===1&&command('shell','sha256sum',publicFile).startsWith(hash),'public copy has exact SHA-256');
 const repeated=await rpc('system.exportTask',{id:task.id});
 check(repeated.uri!==result.uri&&command('shell','sha256sum',publicFile).startsWith(hash),'repeated export never overwrites existing copy');
 command('shell','am','force-stop','com.zera.downloader');socket.close();
 command('shell','am','start','-W','-n','com.zera.downloader/crc64815d0045ae1a360d.MainActivity');await connect();
 state=await rpc('getState');check(state.exportFolder.uri===tree,'persistable directory grant survives process restart');
 await rpc('settings.save',{threads:1,concurrent:2,writeMode:'SharedFile',theme:'light',autoExport:true});
 state=await rpc('direct.add',{url:'http://127.0.0.1:18880/payload.bin',name:'auto-export-fixture.bin'});const autoId=state.tasks.at(-1).id;await finished(autoId);
 let record;
 for(let i=0;i<100;i++){state=await rpc('getState');record=state.exports[autoId];if(record?.state==='Completed')break;if(record?.state==='Failed')throw new Error(record.error);await delay(200);}
 check(record?.state==='Completed'&&command('shell','sha256sum',exportedPath(record.uri,'auto-export-fixture.bin')).startsWith(hash),'automatic completed-file export writes exact bytes');
 await rpc('settings.save',{threads:1,concurrent:2,writeMode:'SharedFile',theme:'light',autoExport:false});
 state=await rpc('direct.add',{url:'http://127.0.0.1:18880/slow/payload.bin',name:'notification-fixture.bin'});const notificationId=state.tasks.at(-1).id;
 await notificationAction('全部暂停');await delay(300);
 state=await rpc('getState');check(state.tasks.find(t=>t.id===notificationId).state==='Paused','notification pause action pauses active download');
 await delay(5500);await notificationAction('继续任务');
 const resumed=await finished(notificationId);check(command('shell','sha256sum',resumed.path).startsWith(hash),'notification resume action retains bytes and completes');
 await rejected('task.deleteLocal',{id:task.id},'local file deletion requires confirmation');
 await rpc('task.deleteLocal',{id:task.id,confirmed:true});
 state=await rpc('getState');check(!state.tasks.some(t=>t.id===task.id)&&command('shell','sha256sum',publicFile).startsWith(hash),'local deletion removes task but preserves exported public copy');
 await rpc('system.clearExportFolder');state=await rpc('getState');
 check(!state.exportFolder.selected&&!state.settings.autoExport,'clearing directory revokes selection and disables auto export');
 await rejected('system.exportTask',{id:notificationId},'export without directory requires fresh user selection');
 fs.writeFileSync(path.join(out,'export-results.json'),JSON.stringify({date:new Date().toISOString(),checks,publicFile},null,2));
 if(fs.existsSync(path.join(out,'export-failure.txt')))fs.unlinkSync(path.join(out,'export-failure.txt'));
 console.log('ALL PUBLIC EXPORT AND NOTIFICATION CHECKS PASSED');
}catch(e){console.error(e);fs.writeFileSync(path.join(out,'export-failure.txt'),e.stack);process.exitCode=1;}finally{socket?.close();}
