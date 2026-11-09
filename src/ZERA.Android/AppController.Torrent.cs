using System.Text.Json;

namespace ZERA.Android;

internal sealed partial class AppController
{
    private readonly object _torrentLock=new();
    private bool _torrentInitialized;
    internal object TorrentCall(string action,JsonElement payload)
    {
        lock(_torrentLock) {
            string request=JsonSerializer.Serialize(new{action,payload,root=Path.Combine(_downloads,"BT"),trackers=Trackers.GetEffective()},Json);
            string result=Com.Zera.Downloader.TorrentBridge.Call(request);
            using var doc=JsonDocument.Parse(result);
            if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("error",out var error))throw new IOException(error.GetString());
            _torrentInitialized=true;
            return doc.RootElement.Clone();
        }
    }
    private bool TorrentActive {
        get {
            if(!_torrentInitialized)return false;
            try {
                var value=(JsonElement)TorrentCall("active",JsonSerializer.SerializeToElement(new{}));
                return value.GetProperty("active").GetBoolean();
            }catch{return false;}
        }
    }
    private void PauseTorrents(){if(_torrentInitialized)TorrentCall("pauseAll",JsonSerializer.SerializeToElement(new{}));}
    internal string TorrentFile(string id,string relative=null)
    {
        var jobs=(JsonElement)TorrentCall("list",JsonSerializer.SerializeToElement(new{}));
        var job=jobs.EnumerateArray().FirstOrDefault(j=>j.GetProperty("id").GetString()==id);
        if(job.ValueKind==JsonValueKind.Undefined||job.GetProperty("state").GetString()!="Completed")throw new IOException("请等待 BT 任务完成后再导出文件。");
        string root=Path.GetFullPath(job.GetProperty("path").GetString());
        if(relative==null)return root;
        string file=Path.GetFullPath(Path.Combine(root,relative));
        if(!file.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!File.Exists(file))throw new FileNotFoundException("结果文件不存在或路径无效。");
        return file;
    }
}
