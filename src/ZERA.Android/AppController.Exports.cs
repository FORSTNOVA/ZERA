using System.Text.Json;

namespace ZERA.Android;

internal sealed partial class AppController
{
    private readonly SemaphoreSlim _exportGate=new(1,1);
    private int _exporting;
    private int _exportPump;
    private readonly HashSet<string> _savingFiles=new();
    private bool IsExporting(string id){lock(_sync)return _savingFiles.Contains(id)||_state.Exports.TryGetValue(id,out var e)&&e.State=="Copying";}
    internal (string id,List<(string path,string relative)> files) ExportSource(JsonElement p)
    {
        if(p.TryGetProperty("torrentId",out var torrent)){
            string id=torrent.GetString(),root=TorrentFile(id);
            if(p.TryGetProperty("file",out var file))return(id,new(){(TorrentFile(id,file.GetString()),Path.GetFileName(file.GetString()))});
            var files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>(path,Path.GetRelativePath(root,path))).Take(100001).ToList();
            if(files.Count>100000)throw new IOException("导出文件数超出限制。");
            return(id,files);
        }
        var task=FindTask(S(p,"id"))??throw new IOException("任务不存在。");
        if(task.State!="Completed"||task.Running)throw new IOException("请等待任务完成后导出。");
        if(!File.Exists(task.Path))throw new IOException("本地文件已移动或删除。");
        return(task.Id,new(){(task.Path,task.Name)});
    }
    internal void SetExportTree(string uri,string name)
    {
        if(!_exportGate.Wait(0))throw new InvalidOperationException("文件正在导出，请稍后更换目录。");
        try{lock(_sync){_state.Settings.ExportTree=uri;_state.Settings.ExportTreeName=name;if(string.IsNullOrEmpty(uri))_state.Settings.AutoExport=false;Save();}}
        finally{_exportGate.Release();}
        Changed?.Invoke();
    }
    internal string ExportTree {get{lock(_sync)return _state.Settings.ExportTree;}}
    internal async Task SaveDocument(JsonElement p,global::Android.Net.Uri destination)
    {
        await _exportGate.WaitAsync();string id=null;
        Interlocked.Increment(ref _exporting);
        try{
            var source=ExportSource(p);if(source.files.Count!=1)throw new IOException("请选择一个结果文件另存为。");
            id=source.id;lock(_sync)_savingFiles.Add(id);EnsureService();
            await Task.Run(()=>DocumentExports.CopyFile(_context.ContentResolver,source.files[0].path,destination));
        }finally{
            if(id!=null)lock(_sync)_savingFiles.Remove(id);
            Interlocked.Decrement(ref _exporting);_exportGate.Release();Changed?.Invoke();
        }
    }
    internal async Task<object> ExportTask(JsonElement p)
    {
        await _exportGate.WaitAsync();string id=null;ExportRecord record=null;
        Interlocked.Increment(ref _exporting);
        try{
            var source=ExportSource(p);id=source.id;string tree;
            lock(_sync){
                tree=_state.Settings.ExportTree;
                if(string.IsNullOrEmpty(tree))throw new IOException("请先在设置中选择公共导出目录。");
                record=new(){State="Copying",Tree=tree};_state.Exports[id]=record;Save();
            }
            EnsureService();Changed?.Invoke();
            var result=await Task.Run(()=>DocumentExports.CopyTree(_context.ContentResolver,tree,id,source.files));
            lock(_sync){record.State="Completed";record.Files=result.files;record.Uri=result.uri;Save();}
            return new{files=result.files,uri=result.uri,message="已导出 "+result.files+" 个文件到公共目录"};
        }catch(Exception ex){if(record!=null)lock(_sync){record.State="Failed";record.Error="导出失败，请检查授权和可用空间；已生成的部分副本保留。";Save();}throw new IOException(record?.Error??ex.Message);}
        finally{Interlocked.Decrement(ref _exporting);_exportGate.Release();Changed?.Invoke();}
    }
    internal void CheckAutoExports()
    {
        lock(_sync)if(!_state.Settings.AutoExport||string.IsNullOrEmpty(_state.Settings.ExportTree))return;
        if(Interlocked.Exchange(ref _exportPump,1)!=0)return;
        List<JsonElement> pending=new();
        try{
            string tree;
            lock(_sync){
                tree=_state.Settings.ExportTree;
                foreach(var task in _state.Tasks.Where(t=>t.State=="Completed"&&!t.Running))
                    if(!_state.Exports.TryGetValue(task.Id,out var record)||record.Tree!=tree)pending.Add(JsonSerializer.SerializeToElement(new{id=task.Id}));
            }
            if(_torrentInitialized){
                var jobs=(JsonElement)TorrentCall("list",JsonSerializer.SerializeToElement(new{}));
                lock(_sync)foreach(var job in jobs.EnumerateArray().Where(j=>j.GetProperty("state").GetString()=="Completed")){
                    string id=job.GetProperty("id").GetString();if(!_state.Exports.TryGetValue(id,out var r)||r.Tree!=tree)pending.Add(JsonSerializer.SerializeToElement(new{torrentId=id}));
                }
            }
        }catch{Interlocked.Exchange(ref _exportPump,0);return;}
        if(pending.Count==0){Interlocked.Exchange(ref _exportPump,0);return;}
        _=Task.Run(async()=>{
            try{
                foreach(var p in pending){try{await ExportTask(p);}catch{Log("自动导出未完成，可在任务中手动重试。");}}
            }finally{Interlocked.Exchange(ref _exportPump,0);}
        });
    }
    internal bool FloatingEnabled {get{lock(_sync)return _state.Settings.FloatingWindow;}}
    internal (string title,string detail,int percent) ProgressSummary()
    {
        long done=0,total=0;double speed=0;int count=0;
        lock(_sync)foreach(var t in _state.Tasks.Where(t=>t.Running&&t.State!="Paused")){count++;done+=t.Done;total+=t.Total;speed+=t.Speed;}
        if(_torrentInitialized){var jobs=(JsonElement)TorrentCall("list",JsonSerializer.SerializeToElement(new{}));foreach(var t in jobs.EnumerateArray().Where(j=>j.GetProperty("state").GetString() is "Downloading" or "Merging")){count++;done+=t.GetProperty("done").GetInt64();total+=t.GetProperty("total").GetInt64();speed+=t.GetProperty("speed").GetInt64();}}
        return(Volatile.Read(ref _exporting)>0?"ZERA 正在导出文件":"ZERA · "+count+" 个下载任务",$"{speed/1024/1024:F1} MiB/s · {done/1024/1024} / {total/1024/1024} MiB",total>0?(int)Math.Clamp(done*100.0/total,0,100):0);
    }
}
