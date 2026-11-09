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
try{
  await connect();
  let state=await rpc('getState');check(state.version==='3.0.0-preview.2','preview.2 native backend');
  const text='https://pan.quark.cn/s/local-qr-fixture';
  const qr=await rpc('test.qrSample',{text});
  check((await rpc('test.decodeQr',{image:qr.png})).text===text,'Android local QR image decoding exact content');
  fs.writeFileSync(path.join(out,'qr.png'),Buffer.from(qr.png,'base64'));
  execFileSync('python',['-c',`from PIL import Image,ImageOps; im=Image.open(r'${path.join(out,'qr.png')}').convert('RGB'); ImageOps.invert(im.rotate(90,expand=True)).save(r'${path.join(out,'qr-inverted.png')}')`]);
  check((await rpc('test.decodeQr',{image:fs.readFileSync(path.join(out,'qr-inverted.png')).toString('base64')})).text===text,'rotated inverted QR image decodes');
  await rejected('test.decodeQr',{image:Buffer.from('not an image').toString('base64')},'invalid QR image returns error');
  state=await rpc('test.cloudSetup',{share:true});
  const selection={ids:['fixture-file'],viewToken:state.viewToken};
  check((await rpc('cloud.transfer',{...selection,path:'/ZERA'})).count===1,'share transfer uses original selected file token');
  let calls=await rpc('test.cloudCalls');check(calls.some(x=>x.includes('fixture-token')&&x.includes('fixture-file')),'transfer bridge includes share context');
  command('reverse','tcp:18880','tcp:18880');
  const oldTasks=new Set((await rpc('getState')).tasks.map(t=>t.id));
  await rpc('cloud.transfer',{...selection,path:'/ZERA',download:true});
  const transferred=(await rpc('getState')).tasks.find(t=>!oldTasks.has(t.id));
  const transferredFile=await finished(transferred.id);
  check(command('shell','sha256sum',transferredFile.path).startsWith(hash),'transfer-and-download resolves saved drive file with exact bytes');
  calls=await rpc('test.cloudCalls');check(calls.some(x=>x.includes('/file/download')&&x.includes('saved-file')),'transferred download binds saved cloud file ID');
  await rejected('cloud.delete',{...selection,confirmed:true},'share source cannot be deleted');
  await rejected('cloud.transfer',{...selection,viewToken:'stale',path:'/ZERA'},'stale account/directory view cannot mutate cloud');
  await rejected('cloud.mkdir',{path:'/safe/../other'},'cloud path traversal is rejected');
  check((await rpc('cloud.mkdir',{path:'/new-folder'})).id==='created-dir','cloud folder creation reaches provider');
  const capacity=await rpc('account.capacity');check(capacity.total===102400&&capacity.used===1024,'capacity comes from native provider');
  state=await rpc('test.cloudSetup',{share:false});
  await rejected('cloud.delete',{ids:['fixture-file'],viewToken:state.viewToken},'cloud deletion requires explicit confirmation');
  check((await rpc('test.cloudCalls')).length===0,'unconfirmed deletion makes no provider request');
  await rpc('cloud.delete',{ids:['fixture-file'],viewToken:state.viewToken,confirmed:true});
  calls=await rpc('test.cloudCalls');check(calls.some(x=>x.includes('/file/delete'))&&!calls.some(x=>x.includes('/recycle/remove')),'default cloud deletion preserves recycle recovery');
  state=await rpc('test.cloudSetup',{share:false});
  await rpc('cloud.delete',{ids:['fixture-file'],viewToken:state.viewToken,confirmed:true,permanent:true});
  calls=await rpc('test.cloudCalls');const purge=calls.find(x=>x.includes('/recycle/remove'));
  check(purge?.includes('selected-record')&&!purge?.includes('unrelated-record'),'permanent deletion purges only selected recycle records');
  await rpc('platform.select',{platform:9});
  if(process.env.ZERA_BT_FIXTURE){
    const jobs=await rpc('test.addTorrent',{torrent:fs.readFileSync(process.env.ZERA_BT_FIXTURE).toString('base64'),split:2});
    const id=jobs.at(-1).id;
    let job=(await rpc('torrent.worker.pause',{id,index:0})).find(t=>t.id===id);
    check(job.workers[0].state==='Paused'&&job.workers[1].state==='Downloading','individual split worker pause leaves sibling running');
    job=(await rpc('torrent.worker.pause',{id,index:1})).find(t=>t.id===id);
    check(job.state==='Paused','all paused workers pause parent');
    job=(await rpc('torrent.worker.resume',{id,index:0})).find(t=>t.id===id);
    check(job.state==='Downloading'&&job.workers[0].state==='Downloading'&&job.workers[1].state==='Paused','individual worker resume keeps sibling paused');
    await rejected('torrent.worker.resume',{id,index:8},'invalid split worker index is rejected');
    await rpc('torrent.pause',{id});
    command('shell','am','force-stop','com.zera.downloader');socket.close();
    command('shell','am','start','-W','-n','com.zera.downloader/crc64815d0045ae1a360d.MainActivity');await connect();
    job=(await rpc('torrent.list')).find(t=>t.id===id);check(job.state==='Paused'&&job.workers.length===2,'split child controls survive process restart');
    await rpc('torrent.remove',{id});
  }
  state=await rpc('getState');
  const complete=state.tasks.find(t=>t.state==='Completed');
  if(complete){
    await rejected('task.deleteLocal',{id:complete.id},'local deletion requires confirmation');
    check(command('shell','test','-f',complete.path)==='','unconfirmed local deletion retains file');
  }
  fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({date:new Date().toISOString(),checks},null,2));
  if(fs.existsSync(path.join(out,'failure.txt')))fs.unlinkSync(path.join(out,'failure.txt'));
  console.log('ALL ROUND 2 CORE CHECKS PASSED');
}catch(error){fs.writeFileSync(path.join(out,'failure.txt'),error.stack);console.error(error);process.exitCode=1;}
finally{socket?.close();}
