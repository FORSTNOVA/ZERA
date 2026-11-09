using System.Text.Json;
using ZERA.Core.Download;
using ZERA.Core.Panels;

namespace ZERA.Android;

internal sealed partial class AppController
{
    private string _viewToken=Guid.NewGuid().ToString("N");
    private void NewView()=>_viewToken=Guid.NewGuid().ToString("N");
    private string[] SelectedIds(JsonElement p)
    {
        if(S(p,"viewToken")!=_viewToken)throw new InvalidOperationException("目录或账号已变化，请重新选择。");
        var ids=p.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()).Distinct().ToArray();
        if(ids.Length==0||ids.Length>1000||ids.Any(id=>!_files.Any(f=>f.Fid==id)))throw new ArgumentException("请选择当前目录中的文件。");
        return ids;
    }
    private static string CloudPath(string path)
    {
        path=(path??"").Trim();
        if(!path.StartsWith('/')||path.Length>1024||path.Contains('\\')||path.Any(char.IsControl)||path.Split('/').Any(p=>p is "." or ".."))throw new ArgumentException("请输入以 / 开头的网盘路径，不包含 ..。");
        return path.TrimEnd('/');
    }
    private async Task<object> CloudOperation(string action,JsonElement p)
    {
        if(_state.Platform!=(int)PanelKind.Quark)throw new NotSupportedException("此操作目前仅支持夸克。");
        var adapter=PanelAdapter.For(Client());
        if(action=="cloud.mkdir"){
            string path=CloudPath(S(p,"path"));if(path.Length==0)throw new ArgumentException("请填写新文件夹路径。");
            string id=await adapter.MkdirAsync(path);return new{id,message="文件夹已创建"};
        }
        string[] ids=SelectedIds(p);
        if(action=="cloud.transfer"){
            if(_shareUrl==null)throw new InvalidOperationException("请先解析分享链接。");
            string path=CloudPath(S(p,"path","/ZERA"));
            string destination=path.Length==0?"0":await adapter.PathFidAsync(path)??await adapter.MkdirAsync(path);
            var saved=new List<string>();
            try{
                foreach(var batch in ids.Chunk(50))saved.AddRange(await adapter.SaveShareAndGetFidsAsync(batch,batch.Select(id=>_files.First(f=>f.Fid==id).Token).ToArray(),destination,_shareId,_shareToken));
            }catch when(saved.Count>0){throw new IOException("已有 "+saved.Count+" 项转存成功，其余未完成；请先检查目标目录再重试。");}
            if(B(p,"download")){
                if(saved.Count!=ids.Length)throw new IOException("转存已完成，但返回文件数与选择不一致；请在我的网盘中选择下载。");
                for(int i=0;i<saved.Count;i++){
                    var original=_files.First(f=>f.Fid==ids[i]);
                    await AddFile(new(){Fid=saved[i],Name=original.Name,Size=original.Size,IsDir=original.IsDir},"",0,drive:true);
                }
            }
            return new{count=saved.Count,message="转存已完成，请在我的网盘中查看"};
        }
        if(action=="cloud.delete"){
            if(_shareUrl!=null)throw new InvalidOperationException("分享文件属于分享者，不能删除。");
            if(!B(p,"confirmed"))throw new InvalidOperationException("请先确认选中的网盘文件。");
            await adapter.DeleteAsync(ids);
            if(B(p,"permanent")){
                try{await adapter.PurgeRecycleAsync(ids);}catch{await Browse(_path);throw new IOException("文件已进入回收站，永久删除未完成；请到夸克客户端检查。");}
            }
            await Browse(_path);return new{count=ids.Length,message=B(p,"permanent")?"所选文件已永久删除":"所选文件已移入回收站"};
        }
        throw new NotSupportedException("未知网盘操作。");
    }
    internal async Task ResumeAll()
    {
        await _commands.WaitAsync();
        try{
            SavedTask[] tasks;lock(_sync)tasks=_state.Tasks.Where(t=>t.State is "Paused" or "Failed").ToArray();
            foreach(var task in tasks){if(task.Running){task.Job?.ResumeJob();task.State="Downloading";}else Start(task);}
            if(_torrentInitialized)TorrentCall("resumeAll",JsonSerializer.SerializeToElement(new{}));
            if(HasActiveWork)EnsureService();Save();
        }finally{_commands.Release();Changed?.Invoke();}
    }
    private void DeleteLocal(JsonElement p)
    {
        var task=FindTask(S(p,"id"))??throw new IOException("任务不存在。");
        if(!B(p,"confirmed"))throw new InvalidOperationException("请先确认删除本地文件。");
        if(task.Running||task.State is not ("Completed" or "Failed" or "Cancelled" or "Paused"))throw new InvalidOperationException("请先结束当前下载后删除本地文件。");
        if(IsExporting(task.Id))throw new InvalidOperationException("文件正在导出，请稍后删除。");
        string root=Path.GetFullPath(_downloads)+Path.DirectorySeparatorChar;
        var paths=new[]{task.Path}.Concat(SegmentedDownloader.EnumeratePartialPaths(task.Path,(string)null)).Select(Path.GetFullPath).Distinct().ToArray();
        if(paths.Any(path=>!path.StartsWith(root,StringComparison.Ordinal)))throw new IOException("本地路径不在应用下载目录中。");
        foreach(string path in paths){if(File.Exists(path))File.Delete(path);else if(Directory.Exists(path))Directory.Delete(path,true);}
        lock(_sync){_state.Tasks.Remove(task);Save();}
    }
}
