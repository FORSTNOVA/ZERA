using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using System.Text.Json;
using Uri=Android.Net.Uri;

namespace ZERA.Android;

[Activity(Label="ZERA",MainLauncher=true,Exported=true,LaunchMode=LaunchMode.SingleTask,ConfigurationChanges=ConfigChanges.Orientation|ConfigChanges.ScreenSize|ConfigChanges.UiMode|ConfigChanges.KeyboardHidden)]
[IntentFilter(new[]{Intent.ActionSend},Categories=new[]{Intent.CategoryDefault},DataMimeType="text/plain")]
[IntentFilter(new[]{Intent.ActionSend},Categories=new[]{Intent.CategoryDefault},DataMimeType="image/*")]
[IntentFilter(new[]{Intent.ActionSend},Categories=new[]{Intent.CategoryDefault},DataMimeType="application/x-bittorrent")]
[IntentFilter(new[]{Intent.ActionView},Categories=new[]{Intent.CategoryDefault},DataMimeType="application/x-bittorrent",DataScheme="content")]
public sealed partial class MainActivity : Activity
{
    private WebView _web;
    private Com.Zera.Downloader.CommandQueue _bridge;
    private System.Threading.Timer _bridgeTimer;
    private WebViewClient _client;
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window.SetSoftInputMode(global::Android.Views.SoftInput.AdjustResize);
        _web=new WebView(this);
#if DEBUG || ZERA_TESTING
        WebView.SetWebContentsDebuggingEnabled(true);
#endif
        _web.Settings.JavaScriptEnabled=true;
        _web.Settings.DomStorageEnabled=false;
        _web.Settings.AllowFileAccess=false;
        _web.Settings.AllowContentAccess=false;
        _web.Settings.SetSupportMultipleWindows(false);
        _web.Settings.MixedContentMode=MixedContentHandling.NeverAllow;
        _bridge=new Com.Zera.Downloader.CommandQueue(_web);
        _web.AddJavascriptInterface(_bridge,"AndroidBridge");
        _bridgeTimer=new System.Threading.Timer(_=>{
            for(int i=0;i<4;i++){string message=_bridge?.Take();if(message==null)break;ProcessMessage(message);}
        },null,100,50);
        _client=new Com.Zera.Downloader.AssetClient(this);
        _web.SetWebViewClient(_client);
        var shell=new global::Android.Widget.FrameLayout(this);
        shell.AddView(_web,new global::Android.Widget.FrameLayout.LayoutParams(-1,-1));
        SetContentView(shell);
        Com.Zera.Downloader.AssetClient.ApplyInsets(shell);
        _web.LoadUrl("https://app.zera.local/index.html");
        MobileApplication.Controller.Changed+=Refresh;
        if(OperatingSystem.IsAndroidVersionAtLeast(33)&&CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)!=Permission.Granted)
            RequestPermissions(new[]{global::Android.Manifest.Permission.PostNotifications},200);
    }
    protected override void OnNewIntent(Intent intent){base.OnNewIntent(intent);Intent=intent;Refresh();_bridge?.Deliver("window.zeraImportIntent?.()");}
    protected override void OnResume(){base.OnResume();MobileApplication.Controller.CheckAutoExports();Refresh();}
    private void Refresh()=>_bridge?.Deliver("window.zeraRefresh?.()");
    protected override void OnDestroy()
    {
        MobileApplication.Controller.Changed-=Refresh;
        _bridgeTimer?.Dispose();_documentPicker?.TrySetCanceled();_web?.RemoveJavascriptInterface("AndroidBridge");_web?.Destroy();_web?.Dispose();_web=null;
        base.OnDestroy();
    }
    private async Task<object> HostAction(string action,JsonElement payload)
    {
        if(action is "system.pickQrImage" or "system.pickExportFolder" or "system.clearExportFolder" or "system.exportTask" or "system.saveFile" or "system.overlayPermission" or "system.pickTorrent" or "system.importSharedTorrent" or "system.sharedText")return await DocumentAction(action,payload);
        if(action=="torrent.files") {
            string root=MobileApplication.Controller.TorrentFile(payload.GetProperty("id").GetString());
            return new{files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Take(10000).Select(file=>new{id=Path.GetRelativePath(root,file),name=Path.GetRelativePath(root,file),size=new FileInfo(file).Length}).ToArray()};
        }
        if(action=="system.theme") {Com.Zera.Downloader.AssetClient.ApplyTheme(this,_web,payload.GetProperty("dark").GetBoolean());return new{applied=true};}
#if ZERA_TESTING
        if(action=="test.decodeQr")return new{text=ImageQrReader.Read(Convert.FromBase64String(payload.GetProperty("image").GetString()))};
        if(action=="test.readTorrentFile") {
            var uri=Uri.Parse("content://com.zera.downloader.files/bt/"+payload.GetProperty("id").GetString()+"/"+System.Uri.EscapeDataString(payload.GetProperty("file").GetString()));
            using var input=ContentResolver.OpenInputStream(uri);
            return new{sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant()};
        }
        if(action=="test.addTorrent") {
            byte[] torrent=Convert.FromBase64String(payload.GetProperty("torrent").GetString());
            if(torrent.Length>2*1024*1024)throw new IOException("测试种子过大。");
            string dir=Path.Combine(FilesDir.AbsolutePath,"torrents");Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,Guid.NewGuid().ToString("N")+".torrent");File.WriteAllBytes(path,torrent);
            StartForegroundService(new Intent(this,typeof(DownloadService)));
            return MobileApplication.Controller.TorrentCall("file",JsonSerializer.SerializeToElement(new{path,split=payload.GetProperty("split").GetInt32()}));
        }
        if(action=="test.inspectState") {
            byte[] data=File.ReadAllBytes(Path.Combine(FilesDir.AbsolutePath,"state.enc"));
            string view=System.Text.Encoding.UTF8.GetString(data);
            return new{encrypted=data.Length>29&&data[0]==1,containsTestPassword=view.Contains("secret"),containsTestAccount=view.Contains("Local WebDAV fixture")};
        }
#endif
        if(action is "system.open" or "system.share") {
            string source;Uri uri;
            if(payload.TryGetProperty("torrentId",out var torrentId)) {
                string relative=payload.GetProperty("file").GetString();source=MobileApplication.Controller.TorrentFile(torrentId.GetString(),relative);
                uri=Uri.Parse("content://com.zera.downloader.files/bt/"+torrentId.GetString()+"/"+System.Uri.EscapeDataString(relative));
            }else{
                string id=payload.GetProperty("id").GetString();var task=MobileApplication.Controller.FindTask(id)??throw new IOException("任务不存在。");
                if(!File.Exists(task.Path))throw new IOException("文件尚未生成或已被移动。");
                if(action=="system.share"&&task.State!="Completed")throw new IOException("请等待文件完成后再导出。");
                source=task.Path;uri=Uri.Parse("content://com.zera.downloader.files/"+id);
            }
            string mime=MimeTypeMap.Singleton.GetMimeTypeFromExtension(Path.GetExtension(source).TrimStart('.').ToLowerInvariant())??"application/octet-stream";
            var intent=action=="system.open"?new Intent(Intent.ActionView).SetDataAndType(uri,mime):new Intent(Intent.ActionSend).SetType(mime).PutExtra(Intent.ExtraStream,uri);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            RunOnUiThread(()=>{try{StartActivity(Intent.CreateChooser(intent,action=="system.open"?"打开文件":"保存或分享文件"));}catch{global::Android.Widget.Toast.MakeText(this,"没有可打开此文件的应用",global::Android.Widget.ToastLength.Long).Show();}});
            return new{opened=true};
        }
        return await MobileApplication.Controller.Dispatch(action,payload);
    }
        private int _pending;
        private void ProcessMessage(string message)
        {
            if(message==null||message.Length>256*1024)return;
            if(Interlocked.Increment(ref _pending)>12){Interlocked.Decrement(ref _pending);return;}
            _=Task.Run(async()=>{
                JsonElement id=default;object reply;
                try {
                    using var doc=JsonDocument.Parse(message,new JsonDocumentOptions{MaxDepth=24});var root=doc.RootElement;
                    id=root.GetProperty("id").Clone();
                    var payload=root.TryGetProperty("payload",out var p)?p.Clone():JsonSerializer.SerializeToElement(new{});
                    var result=await HostAction(root.GetProperty("action").GetString(),payload);
                    reply=new{ok=true,data=result};
                }catch(System.OperationCanceledException){reply=new{ok=false,error="操作已取消"};}
                catch(Exception ex){reply=new{ok=false,error=AppControllerError(ex)};}
                finally{Interlocked.Decrement(ref _pending);}
                if(id.ValueKind!=JsonValueKind.Undefined) {
                    try {
                        string js="window.zeraResolve?.("+id.GetRawText()+","+JsonSerializer.Serialize(reply,AppController.Json)+")";
                        _bridge?.Deliver(js);
                    }catch(Exception ex){global::Android.Util.Log.Error("ZERA.Native","Reply failure: "+ex.GetType().Name);}
                }
            });
        }
        private static string AppControllerError(Exception ex)=>System.Text.RegularExpressions.Regex.Replace(ex.Message,@"https?://\S+","[地址已隐藏]");
}
