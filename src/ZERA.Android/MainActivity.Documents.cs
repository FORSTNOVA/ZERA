using Android.App;
using Android.Content;
using Android.Provider;
using System.Text.Json;
using Uri=Android.Net.Uri;

namespace ZERA.Android;

public sealed partial class MainActivity
{
    private readonly object _pickerLock=new();
    private TaskCompletionSource<(Uri uri,ActivityFlags flags)> _documentPicker;
    private async Task<(Uri uri,ActivityFlags flags)> PickDocument(Intent intent)
    {
        TaskCompletionSource<(Uri uri,ActivityFlags flags)> picker;
        lock(_pickerLock){
            if(_documentPicker!=null)throw new InvalidOperationException("已有系统文件选择窗口，请先完成或取消。");
            picker=new(TaskCreationOptions.RunContinuationsAsynchronously);_documentPicker=picker;
        }
        try{
            RunOnUiThread(()=>{try{StartActivityForResult(intent,110);}catch(Exception ex){picker.TrySetException(ex);}});
            return await picker.Task;
        }finally{lock(_pickerLock)if(_documentPicker==picker)_documentPicker=null;}
    }
    protected override void OnActivityResult(int requestCode,Result resultCode,Intent data)
    {
        base.OnActivityResult(requestCode,resultCode,data);
        if(requestCode!=110)return;
        if(resultCode!=Result.Ok||data?.Data==null){_documentPicker?.TrySetCanceled();return;}
        if(data.Data.Scheme!="content"){_documentPicker?.TrySetException(new IOException("请选择系统文档提供器中的文件。"));return;}
        _documentPicker?.TrySetResult((data.Data,data.Flags));
    }
    private Uri IncomingUri()=>Intent?.Action==Intent.ActionView?Intent.Data:
        OperatingSystem.IsAndroidVersionAtLeast(33)?Intent?.GetParcelableExtra(Intent.ExtraStream,Java.Lang.Class.FromType(typeof(Uri))) as Uri:Intent?.GetParcelableExtra(Intent.ExtraStream) as Uri;
    private async Task<object> DocumentAction(string action,JsonElement p)
    {
        var controller=MobileApplication.Controller;
        if(action=="system.overlayPermission"){
            bool granted=Settings.CanDrawOverlays(this);
            if(!granted)RunOnUiThread(()=>{try{StartActivity(new Intent(Settings.ActionManageOverlayPermission,Uri.Parse("package:"+PackageName)));}catch{global::Android.Widget.Toast.MakeText(this,"请在系统设置中允许 ZERA 显示在其他应用上层",global::Android.Widget.ToastLength.Long).Show();}});
            return new{granted,requested=!granted};
        }
        if(action=="system.pickExportFolder"){
            var result=await PickDocument(new Intent(Intent.ActionOpenDocumentTree).AddFlags(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission|ActivityFlags.GrantPersistableUriPermission|ActivityFlags.GrantPrefixUriPermission));
            var flags=result.flags&(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission);
            if((flags&ActivityFlags.GrantWriteUriPermission)==0)throw new IOException("所选目录未授予写入权限。");
            ContentResolver.TakePersistableUriPermission(result.uri,flags);
            string name=DocumentExports.TreeName(ContentResolver,result.uri);
            string previous=controller.ExportTree;
            controller.SetExportTree(result.uri.ToString(),name);
            if(previous!=result.uri.ToString())ReleaseTree(previous);
            return new{name,uri=result.uri.ToString()};
        }
        if(action=="system.clearExportFolder"){string previous=controller.ExportTree;controller.SetExportTree(null,null);ReleaseTree(previous);return new{cleared=true};}
        if(action=="system.exportTask")return await controller.ExportTask(p);
        if(action=="system.saveFile"){
            var source=controller.ExportSource(p);
            if(source.files.Count!=1)throw new IOException("请选择一个结果文件另存为。");
            string file=source.files[0].path;
            var result=await PickDocument(new Intent(Intent.ActionCreateDocument).SetType(DocumentExports.Mime(file)).AddCategory(Intent.CategoryOpenable).PutExtra(Intent.ExtraTitle,Path.GetFileName(file)));
            await controller.SaveDocument(p,result.uri);return new{saved=true,message="文件已另存为"};
        }
        if(action=="system.sharedText"){
            if(Intent?.Action==Intent.ActionSend&&Intent.Type?.StartsWith("image/")==true){
                var uri=IncomingUri();if(uri?.Scheme!="content")throw new IOException("图片分享缺少有效文件授权。");
                return new{text=await Task.Run(()=>ImageQrReader.Read(this,uri)),kind="qr"};
            }
            if((Intent?.Action==Intent.ActionSend||Intent?.Action==Intent.ActionView)&&Intent.Type=="application/x-bittorrent")return new{text="",kind="torrent"};
            return new{text=Intent?.Action==Intent.ActionSend?Intent.GetStringExtra(Intent.ExtraText)??"":"",kind="text"};
        }
        if(action=="system.pickQrImage"){
            var selected=await PickDocument(new Intent(Intent.ActionOpenDocument).SetType("image/*").AddCategory(Intent.CategoryOpenable));
            return new{text=await Task.Run(()=>ImageQrReader.Read(this,selected.uri))};
        }
        Uri torrent;
        if(action=="system.importSharedTorrent")torrent=IncomingUri();
        else torrent=(await PickDocument(new Intent(Intent.ActionOpenDocument).SetType("*/*").AddCategory(Intent.CategoryOpenable))).uri;
        if(torrent?.Scheme!="content")throw new IOException("种子文件缺少有效读取授权。");
        string path=await Task.Run(()=>CopyTorrent(torrent));
        try{
            StartForegroundService(new Intent(this,typeof(DownloadService)));
            int split=p.TryGetProperty("split",out var count)?count.GetInt32():1;
            return controller.TorrentCall("file",JsonSerializer.SerializeToElement(new{path,split}));
        }finally{File.Delete(path);}
    }
    private string CopyTorrent(Uri uri)
    {
        string dir=Path.Combine(CacheDir.AbsolutePath,"torrents");Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,Guid.NewGuid().ToString("N")+".torrent");
        try{
            using var source=ContentResolver.OpenInputStream(uri);using var dest=File.Create(path);var bytes=new byte[8192];int n;long total=0;
            while((n=source.Read(bytes,0,bytes.Length))>0){total+=n;if(total>16*1024*1024)throw new IOException("种子文件超过 16 MiB。");dest.Write(bytes,0,n);}
            dest.Flush(true);return path;
        }catch{File.Delete(path);throw;}
    }
    private void ReleaseTree(string uri)
    {if(!string.IsNullOrEmpty(uri))try{ContentResolver.ReleasePersistableUriPermission(Uri.Parse(uri),ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission);}catch(Java.Lang.SecurityException){}}
}
