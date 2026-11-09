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
function overlayFrame(){
 const block=command('shell','dumpsys','window','windows').split(/(?=  Window #)/).find(b=>b.includes('package=com.zera.downloader appop=SYSTEM_ALERT_WINDOW')&&b.includes('ty=APPLICATION_OVERLAY')&&b.includes('mViewVisibility=0x0'));
 const bounds=block?.match(/Frames:[^\n]* frame=\[(\d+),(\d+)\]\[(\d+),(\d+)\]/);
 return bounds?bounds.slice(1).map(Number):null;
}
function point(frame,fraction){return[String(Math.round(frame[0]+(frame[2]-frame[0])*fraction)),String(Math.round(frame[1]+(frame[3]-frame[1])*.8))];}
try{
 command('shell','am','start','-W','-n','com.zera.downloader/crc64815d0045ae1a360d.MainActivity');
 command('reverse','tcp:18880','tcp:18880');await connect();
 command('shell','appops','set','com.zera.downloader','SYSTEM_ALERT_WINDOW','default');
 let state=await rpc('getState');
 if(!state.overlayAllowed)await rejected('settings.save',{...state.settings,floatingWindow:true},'overlay cannot be enabled without OS permission');
 command('shell','appops','set','com.zera.downloader','SYSTEM_ALERT_WINDOW','allow');
 await rpc('settings.save',{threads:1,concurrent:2,writeMode:'SharedFile',theme:'light',floatingWindow:true});
 state=await rpc('direct.add',{url:'http://127.0.0.1:18880/hold/payload.bin',name:'overlay-fixture.bin'});const id=state.tasks.at(-1).id;
 await delay(6500);
 // UIAutomator's active-window dump omits non-focusable overlays. Use the actual
 // WindowManager frame and the button positions verified in the native screenshot.
 const frame=overlayFrame();check(!!frame,'native floating window is displayed');const pause=point(frame,.19);
 command('shell','screencap','-p','/sdcard/zera-overlay.png');command('pull','/sdcard/zera-overlay.png',path.join(out,'overlay.png'));
 command('shell','input','tap',...pause);await delay(150);
 check((await rpc('getState')).tasks.find(t=>t.id===id).state==='Paused','floating pause button controls native task');
 command('shell','input','tap',...pause);await delay(400);
 check(['Downloading','Queued'].includes((await rpc('getState')).tasks.find(t=>t.id===id).state),'floating continue button resumes native task');
 command('shell','input','tap',...point(overlayFrame(),.81));await delay(2200);
 check(!overlayFrame(),'closed floating window stays hidden during service updates');
 await rpc('task.cancel',{id});await rpc('settings.save',{threads:1,concurrent:2,writeMode:'SharedFile',theme:'light',floatingWindow:false});
 command('shell','appops','set','com.zera.downloader','SYSTEM_ALERT_WINDOW','default');
 fs.writeFileSync(path.join(out,'overlay-results.json'),JSON.stringify({date:new Date().toISOString(),checks},null,2));
 if(fs.existsSync(path.join(out,'overlay-failure.txt')))fs.unlinkSync(path.join(out,'overlay-failure.txt'));
 console.log('ALL OVERLAY CHECKS PASSED');
}catch(e){console.error(e);fs.writeFileSync(path.join(out,'overlay-failure.txt'),e.stack);process.exitCode=1;}finally{socket?.close();}
