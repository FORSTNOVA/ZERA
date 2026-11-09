using Android.App;
using Android.Content;
using System.Text.Json;
using ZERA.Core.Api;
using ZERA.Core.Download;
using ZERA.Core.Models;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA.Android;

internal sealed partial class AppController : IProgress<JobUpdate>
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly Context _context;
    private readonly SecureStore _store;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Dictionary<string, IPanelClient> _clients = new();
    private readonly Queue<string> _logs = new();
    private AppState _state;
    private string _loadError;
    private DownloadScheduler _scheduler;
    private readonly string _downloads;
    private List<QuarkFileEntry> _files = new();
    private string _path = "", _shareUrl, _passcode, _shareId, _shareToken;
    private DateTime _lastSave;
    internal readonly TrackerStore Trackers;
    private readonly System.Threading.Timer _trackerTimer;
    internal event Action Changed;
    public AppController(Context context)
    {
        _context = context;
        _downloads = Path.Combine(context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? context.FilesDir.AbsolutePath, "Downloads");
        Directory.CreateDirectory(_downloads);
        _store = new SecureStore(Path.Combine(context.FilesDir.AbsolutePath, "state.enc"));
        try { _state = JsonSerializer.Deserialize<AppState>(_store.Read() ?? "{}", Json) ?? new(); }
        catch { _state = new(); _loadError = "本地加密资料无法读取，已保留原文件。请先备份应用数据后排查，不会覆盖保存。"; }
        foreach(var export in _state.Exports.Values.Where(e=>e.State=="Copying")){export.State="Failed";export.Error="上次导出被中断，部分副本已保留，请手动重试。";}
        foreach (var task in _state.Tasks)
            if (task.State is "Downloading" or "Queued" or "Cancelling") task.State = "Paused";
        _scheduler = new DownloadScheduler(_state.Settings.Concurrent) { Log = message => Log("下载引擎：" + Redact(message)) };
        Trackers = new TrackerStore(Path.Combine(context.FilesDir.AbsolutePath, "bt-state"));
        _trackerTimer=new System.Threading.Timer(async _=>{
            try{await Trackers.UpdateAsync(false);}catch{Log("Tracker 自动检查失败，保留原缓存。");}
        },null,TimeSpan.FromMinutes(15),TimeSpan.FromMinutes(15));
    }
    private static string S(JsonElement p, string key, string fallback = "") => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) ? v.ToString() : fallback;
    private static int I(JsonElement p, string key, int fallback = 0) => int.TryParse(S(p, key), out int value) ? value : fallback;
    private static bool B(JsonElement p, string key) => S(p, key).Equals("true", StringComparison.OrdinalIgnoreCase);
    private void Save() { lock (_sync) { if (_loadError != null) throw new IOException(_loadError); _store.Write(JsonSerializer.Serialize(_state, Json)); _lastSave = DateTime.UtcNow; } }
    private void Log(string text) { lock (_sync) { _logs.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text); while (_logs.Count > 150) _logs.Dequeue(); } }
    private static string Redact(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"https?://\S+|(?i)(cookie|authorization|token|bduss|stoken|secret)[=:]\S+", "[已隐藏]");
    private SavedAccount Active => _state.Accounts.FirstOrDefault(a => a.Id == _state.ActiveAccount && a.Platform == _state.Platform);
    private IPanelClient Client(SavedAccount account = null, int? platform = null)
    {
        if(account==null&&platform==null)account=Active;
        var kind = (PanelKind)(platform ?? account?.Platform ?? _state.Platform);
        if (account != null && account.Platform != (int)kind) throw new InvalidOperationException("账号与网盘不匹配，请选择对应账号。");
        if (account == null && kind != PanelKind.Lanzou) throw new InvalidOperationException("请先在账号页登录或配置此网盘。");
        string key = account?.Id ?? "anonymous-lanzou";
        if (_clients.TryGetValue(key, out var client)) return client;
        client = PanelRegistry.Create(kind, account?.Credential ?? "");
        _clients[key] = client;
        if (client is NativeCloudClient native) native.CredentialsChanged += () => { lock (_sync) { account.Credential = native.CookieStr; Save(); } };
        return client;
    }
    private object FilesView() => _files.Select(f => new { id = f.Fid, name = f.Name, size = f.Size, directory = f.IsDir }).ToArray();
    internal object StateView()
    {
        lock (_sync) return new {
            platforms = PanelRegistry.All().Select(p => new { id = (int)p.Kind, name = p.DisplayName, share = p.SupportsShare, drive = p.SupportsDrive,
                qr = p.SupportsQrLogin, password = p.SupportsPasswordLogin, cookie=p.SupportsCookieLogin,
                native=NativeCloudAccount.Supports(p.Kind), cloudOps=p.Kind==PanelKind.Quark, hint = p.LoginHint }).ToArray(),
            platform = _state.Platform, activeAccount = _state.ActiveAccount,
            accounts = _state.Accounts.Select(a => new { id = a.Id, platform = a.Platform, name = a.Name }).ToArray(),
            tasks = _state.Tasks.Select(t => new { id = t.Id, name = t.Name, state = t.State, done = t.Done, total = t.Total, speed = t.Speed, error = t.Error, path = t.Path }).ToArray(),
            settings = _state.Settings, files = FilesView(), path = _path, shareMode=_shareUrl!=null,viewToken=_viewToken,
            exportFolder=new{selected=!string.IsNullOrEmpty(_state.Settings.ExportTree),name=_state.Settings.ExportTreeName,uri=_state.Settings.ExportTree},exports=_state.Exports,
            systemDark=(_context.Resources.Configuration.UiMode&global::Android.Content.Res.UiMode.NightMask)==global::Android.Content.Res.UiMode.NightYes,
            overlayAllowed=global::Android.Provider.Settings.CanDrawOverlays(_context),
            logs = _logs.ToArray(), error = _loadError,
            downloadDirectory = _downloads, version = "3.0.0-preview.2"
        };
    }
    internal async Task<object> Dispatch(string action, JsonElement p)
    {
#if ZERA_TESTING
        if(action is "test.cloudSetup" or "test.cloudCalls" or "test.qrSample")return await Testing(action,p);
#endif
        if (action == "getState") return StateView();
        if (action.StartsWith("login.")) return await Login(action, p);
        if(action=="task.resumeAll"){await ResumeAll();return StateView();}
        if(action=="task.pauseAll"){PauseAll();return StateView();}
        await _commands.WaitAsync();
        try
        {
            switch (action)
            {
                case "platform.select":
                    int platform = I(p, "platform");
                    if (PanelRegistry.Describe((PanelKind)platform) == null) throw new ArgumentException("未知平台。");
                    lock(_sync){_state.Platform = platform; _state.ActiveAccount = _state.LastAccounts.GetValueOrDefault(platform); ClearFiles(); Save();} return StateView();
                case "browse": return await Browse(S(p, "id"));
                case "share.parse": return await ParseShare(S(p, "text"), S(p, "passcode"));
                case "cloud.mkdir":case "cloud.transfer":case "cloud.delete":return await CloudOperation(action,p);
                case "account.capacity":
                    var capacity=await Client().GetCapacityAsync();return new{capacity.total,capacity.used};
                case "task.clearCompleted":lock(_sync){_state.Tasks.RemoveAll(t=>!t.Running&&t.State is "Completed" or "Cancelled");Save();}return StateView();
                case "task.deleteLocal":DeleteLocal(p);return StateView();
                case "download.add":
                    foreach (string id in p.GetProperty("ids").EnumerateArray().Select(v => v.GetString()))
                    {
                        var file = _files.FirstOrDefault(f => f.Fid == id) ?? throw new ArgumentException("目录已变化，请重新选择文件。");
                        await AddFile(file, "", 0);
                    }
                    return StateView();
                case "direct.add":
                    string url = S(p, "url");
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new ArgumentException("请输入有效的 HTTP(S) 文件直链。");
                    string name = S(p, "name"); if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
                    var direct = new SavedTask { Name = FileName(name), DirectUrl = url, Path = NewPath(name), WriteMode = _state.Settings.WriteMode };
                    lock(_sync) _state.Tasks.Add(direct); Save(); Start(direct); return StateView();
                case "task.pause": case "task.resume": case "task.cancel": case "task.remove":
                    var task = _state.Tasks.FirstOrDefault(t => t.Id == S(p, "id")) ?? throw new ArgumentException("任务不存在。");
                    if (action == "task.pause") { task.Job?.PauseJob(); task.State = "Paused"; task.Speed = 0; }
                    if (action == "task.resume") { if (task.Running) { task.Job?.ResumeJob(); task.State = "Downloading"; EnsureService(); } else Start(task); }
                    if (action is "task.cancel" or "task.remove") {
                        if (task.Running) { task.State = "Cancelling"; task.Job?.CancelJob(); task.Job?.ResumeJob(); }
                        else if (action == "task.remove") {lock(_sync)_state.Tasks.Remove(task);}
                        else task.State = "Cancelled";
                    }
                    Save(); return StateView();
                case "account.webdav":
                    return await SaveAccount(2, S(p, "name"), new WebDavAccount { Name = S(p,"name"), Endpoint=S(p,"endpoint"), Username=S(p,"username"), Password=S(p,"password"), AllowHttp=B(p,"allowHttp") }.Serialize());
                case "account.save": return await SaveAccount(I(p, "platform"), S(p,"name"), S(p,"credential"));
                case "account.select":
                    var selected = _state.Accounts.FirstOrDefault(a => a.Id == S(p,"id")) ?? throw new ArgumentException("账号不存在。");
                    var valid = await Client(selected).ValidateAsync(); if (!valid.ok) throw new InvalidOperationException("账号已失效，请重新登录。");
                    lock(_sync){_state.Platform = selected.Platform; _state.ActiveAccount = selected.Id; _state.LastAccounts[selected.Platform] = selected.Id; ClearFiles(); Save();} return StateView();
                case "account.remove":
                    string accountId = S(p,"id");
                    if (_state.Tasks.Any(t => t.AccountId == accountId && t.State is not ("Completed" or "Cancelled"))) throw new InvalidOperationException("此账号还有未结束任务，请先完成或取消。");
                    lock(_sync)_state.Accounts.RemoveAll(a => a.Id == accountId);
                    if (_clients.Remove(accountId, out var old)) old.Dispose();
                    lock(_sync)foreach (var key in _state.LastAccounts.Where(x => x.Value == accountId).Select(x=>x.Key).ToArray()) _state.LastAccounts.Remove(key);
                    if (_state.ActiveAccount == accountId) { _state.ActiveAccount = null; ClearFiles(); } Save(); return StateView();
                case "settings.save":
                    bool active=_state.Tasks.Any(t=>t.Running);
                    if(active&&(I(p,"threads",8)!=_state.Settings.Threads||I(p,"concurrent",2)!=_state.Settings.Concurrent||S(p,"writeMode")!=_state.Settings.WriteMode))throw new InvalidOperationException("请在当前下载结束后修改下载参数。");
                    if(B(p,"autoExport")&&string.IsNullOrEmpty(_state.Settings.ExportTree))throw new InvalidOperationException("请先选择公共导出目录。");
                    if(B(p,"floatingWindow")&&!global::Android.Provider.Settings.CanDrawOverlays(_context))throw new InvalidOperationException("请先允许显示在其他应用上层。");
                    _state.Settings.Threads = Math.Clamp(I(p,"threads",8),1,64); _state.Settings.Concurrent = Math.Clamp(I(p,"concurrent",2),1,8);
                    _state.Settings.WriteMode = S(p,"writeMode") == "SegmentedFiles" ? "SegmentedFiles" : "SharedFile";
                    _state.Settings.Theme = S(p,"theme") is "light" or "dark" ? S(p,"theme") : "system";
                    if(p.TryGetProperty("autoExport",out _)){
                        _state.Settings.AutoExport=B(p,"autoExport");
                    }
                    if(p.TryGetProperty("floatingWindow",out _)){
                        _state.Settings.FloatingWindow=B(p,"floatingWindow");
                    }
                    if(!active){_scheduler.Dispose(); _scheduler = new DownloadScheduler(_state.Settings.Concurrent);} Save();CheckAutoExports(); return StateView();
                case "membership.query": return await PanelAdapter.For(Client()).QueryMembershipAsync();
                case "links.export": return await ExportLinks(p);
                case "tracker.get": return Trackers.Load();
                case "tracker.save": var trackerSettings = p.Deserialize<TrackerSettings>(Json); Trackers.Save(trackerSettings); return Trackers.Load();
                case "tracker.update": return await Trackers.UpdateAsync(true);
                case "torrent.list": var list=TorrentCall("list",p);CheckAutoExports();return list;
                case "torrent.add": EnsureService(); return TorrentCall("add", p);
                case "torrent.pause": case "torrent.resume": case "torrent.remove":case "torrent.worker.pause":case "torrent.worker.resume":
                    var torrentResult=TorrentCall(action[8..],p);if(action.EndsWith("resume"))EnsureService();return torrentResult;
                default: throw new NotSupportedException("此操作尚未接入安卓端：" + action);
            }
        }
        finally { _commands.Release(); Changed?.Invoke(); }
    }
    private void ClearFiles() { _files = new(); _path = ""; _shareUrl = _passcode = _shareId = _shareToken = null;NewView(); }
    private async Task<object> Browse(string id)
    {
        if (_shareUrl != null) _files = await PanelAdapter.For(Client()).ShareDetailAsync(_shareId, _shareToken, string.IsNullOrEmpty(id) ? "0" : id);
        else _files = await Client().ListDirAsync(id, 100);
        _path = id;NewView(); return new { files = FilesView(), path = id,viewToken=_viewToken };
    }
    private async Task<object> ParseShare(string text, string passcode)
    {
        PanelKind kind = PanelRegistry.Detect(text) ?? (PanelKind)_state.Platform;
        if (!PanelRegistry.Describe(kind).SupportsShare) throw new ArgumentException("此平台暂不支持分享解析。");
        if ((int)kind != _state.Platform) { _state.Platform = (int)kind; _state.ActiveAccount = _state.LastAccounts.GetValueOrDefault((int)kind); }
        ClearFiles();
        var info = ShareUrlParser.Parse(text); passcode = string.IsNullOrEmpty(passcode) ? info.Passcode : passcode;
        var adapter = PanelAdapter.For(Client());
        if (kind == PanelKind.Baidu) await adapter.OpenShareAsync(text, passcode);
        else _shareToken = await adapter.GetStokenAsync(info.PwdId, passcode);
        _shareId = info.PwdId; _shareUrl = text; _passcode = passcode;
        _files = await adapter.ShareDetailAsync(_shareId, _shareToken, info.StartFid ?? "0"); Save();
        return new { files = FilesView(), path = "" };
    }
    private async Task<object> SaveAccount(int platform, string name, string credential, CancellationToken cancel=default)
    {
        var kind = (PanelKind)platform;
        var bound=_state.Accounts.FirstOrDefault(a=>a.Platform==platform&&a.Credential==credential);
        if(bound!=null&&_state.Tasks.Any(t=>t.AccountId==bound.Id&&t.State is not ("Completed" or "Cancelled")))throw new InvalidOperationException("此账号还有未结束任务，请先完成或取消后再修改登录资料。");
        using var candidate = PanelRegistry.Create(kind, credential);
        var login = await PanelAdapter.For(candidate).LoginAsync(cancel);
        cancel.ThrowIfCancellationRequested();
        if (!login.Valid) throw new InvalidOperationException("账号校验失败，请检查登录资料。");
        var existing = _state.Accounts.FirstOrDefault(a => a.Platform == platform && a.Credential == credential);
        if (existing == null) { existing = new SavedAccount { Platform=platform }; lock(_sync)_state.Accounts.Add(existing); }
        if (_clients.Remove(existing.Id, out var previous)) previous.Dispose();
        existing.Credential = candidate.CookieStr;
        existing.Name = string.IsNullOrWhiteSpace(name) ? login.Name ?? PanelRegistry.Describe(kind).DisplayName : name.Trim();
        lock(_sync){_state.Platform = platform; _state.ActiveAccount = existing.Id; _state.LastAccounts[platform] = existing.Id;
        ClearFiles(); Save();} return StateView();
    }
    private static string FileName(string name)
    {
        name = Path.GetFileName((name ?? "download.bin").Replace('\\','/'));
        name = string.Concat(name.Select(c => c < 32 || "/\\:*?\"<>|".Contains(c) ? '_' : c)).Trim('.',' ');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }
    private string NewPath(string name) => Path.Combine(_downloads, Guid.NewGuid().ToString("N")[..8], FileName(name));
    private async Task AddFile(QuarkFileEntry file, string relative, int depth,string directory=null,bool drive=false)
    {
        if (depth > 40 || _state.Tasks.Count > 10000) throw new InvalidOperationException("目录层级或任务数超出限制。");
        directory??=Path.GetDirectoryName(NewPath(file.Name));
        if (file.IsDir)
        {
            var children = drive||_shareUrl == null ? await Client().ListDirAsync(file.Fid,100) : await PanelAdapter.For(Client()).ShareDetailAsync(_shareId,_shareToken,file.Fid);
            foreach (var child in children) await AddFile(child, Path.Combine(relative,FileName(file.Name)),depth+1,directory,drive);
            return;
        }
        bool fromShare=_shareUrl!=null&&!drive;
        var pending = new PendingInfo { Panel=(PanelKind)_state.Platform, AccountKey=Active?.Id, Fid=file.Fid, Token=file.Token,
            PwdId=fromShare?_shareId:null, Stoken=fromShare?_shareToken:null, Passcode=fromShare?_passcode:null, FromShare=fromShare };
        var task = new SavedTask { Name=FileName(file.Name), AccountId=Active?.Id, Platform=_state.Platform, Total=file.Size,
            Path=Path.Combine(directory,relative,FileName(file.Name)), Pending=pending,
            ShareUrl=fromShare?_shareUrl:null, Passcode=fromShare?_passcode:null, WriteMode=_state.Settings.WriteMode };
        if(_state.Tasks.Any(t=>t.Path==task.Path))task.Path=Path.Combine(Path.GetDirectoryName(task.Path),Path.GetFileNameWithoutExtension(task.Path)+"-"+task.Id[..8]+Path.GetExtension(task.Path));
        lock(_sync)_state.Tasks.Add(task); Save(); Start(task);
    }
    private async Task<DownloadLink> Resolve(SavedTask task, IPanelClient client)
    {
        if (task.ShareUrl == null || client.Kind == PanelKind.Lanzou) return await client.ResolveDriveFileAsync(task.Pending.Fid);
        var adapter = PanelAdapter.For(client);
        if (client.Kind == PanelKind.Baidu) await adapter.OpenShareAsync(task.ShareUrl, task.Passcode);
        else task.Pending.Stoken = await adapter.GetStokenAsync(task.Pending.PwdId, task.Passcode);
        var links = await adapter.GetShareDownloadAsync(new[] {task.Pending.Fid},new[] {task.Pending.Token},task.Pending.PwdId,task.Pending.Stoken);
        return links.FirstOrDefault() ?? throw new IOException("未返回文件直链，请重新解析分享。");
    }
    private void Start(SavedTask task)
    {
        if (task.Running) return;
        task.Running = true; task.State="Queued"; task.Error=null; EnsureService(); Save();
        _ = Task.Run(async () => {
            try {
                IPanelClient client=null; DownloadRequestContext context;
                if (task.DirectUrl != null) context = new DownloadRequestContext {Url=task.DirectUrl, UserAgent="ZERA/3.0", Origin="",Referer=""};
                else {
                    SavedAccount account; lock(_sync) account=_state.Accounts.FirstOrDefault(a=>a.Id==task.AccountId);
                    if(account==null && task.Platform!=(int)PanelKind.Lanzou) throw new InvalidOperationException("缺少此任务的原账号，请重新登录。");
                    await _commands.WaitAsync();
                    try { client=Client(account,task.Platform); var link=await Resolve(task,client); context=client.BuildDownloadContext(); context.Url=link.DownloadUrl; }
                    finally {_commands.Release();}
                }
                var job = new DownloadJob { Id=task.Id, Name=task.Name, Url=context.Url, Dest=task.Path, Cookie=context.Cookie,
                    Authorization=context.Authorization, CredentialScope=context.CredentialScope, UserAgent=context.UserAgent, Referer=context.Referer,
                    Origin=context.Origin, FallbackUserAgent=context.FallbackUserAgent,FallbackReferer=context.FallbackReferer,FallbackOrigin=context.FallbackOrigin,
                    Threads=context.SuggestedThreads>0 ? Math.Min(context.SuggestedThreads,_state.Settings.Threads):_state.Settings.Threads,
                    MaxRequestBytes=context.MaxRequestBytes, Pending=task.Pending, WriteMode=Enum.Parse<WriteMode>(task.WriteMode) };
                if(client!=null) job.LinkRefresher=async ct=> { await _commands.WaitAsync(ct);try{return (await Resolve(task,client)).DownloadUrl;}finally{_commands.Release();} };
                lock(_sync) {task.Job=job; if(task.State=="Paused")job.PauseJob();if(task.State=="Cancelling")job.CancelJob();}
                await _scheduler.RunAllAsync(new[]{job},this,CancellationToken.None);
            } catch(Exception ex) {lock(_sync){task.State="Failed";task.Error=Redact(ex.Message);}}
            finally {lock(_sync){task.Running=false;task.Speed=0;task.Job=null;Save();} Changed?.Invoke();}
        });
    }
    public void Report(JobUpdate update)
    {
        lock(_sync) {
            var task=_state.Tasks.FirstOrDefault(t=>t.Id==update.Job.Id);if(task==null)return;
            if(task.State is not ("Paused" or "Cancelling") || update.State is JobState.Completed or JobState.Cancelled or JobState.Failed)task.State=update.State.ToString();
            if(update.Progress!=null){task.Done=update.Progress.Done;task.Total=update.Progress.Total;task.Speed=task.State=="Paused"?0:update.Progress.Speed;}
            if(!string.IsNullOrEmpty(update.Error))task.Error=Redact(update.Error);
            if((DateTime.UtcNow-_lastSave).TotalSeconds>=2)Save();
        }
    }
    internal bool HasActiveWork { get {lock(_sync)return _state.Tasks.Any(t=>t.Running&&t.State!="Paused") || TorrentActive||Volatile.Read(ref _exporting)>0||Volatile.Read(ref _exportPump)>0;} }
    internal void PauseAll() {lock(_sync){foreach(var t in _state.Tasks.Where(t=>t.Running)){t.Job?.PauseJob();t.State="Paused";t.Speed=0;}Save();}PauseTorrents();Changed?.Invoke();}
    internal SavedTask FindTask(string id) {lock(_sync)return _state.Tasks.FirstOrDefault(t=>t.Id==id);}
    private void EnsureService() => _context.StartForegroundService(new Intent(_context,typeof(DownloadService)));
    private async Task<object> ExportLinks(JsonElement p)
    {
        var links = new List<LinkExporter.Item>();var client=Client();
        foreach(var id in p.GetProperty("ids").EnumerateArray().Select(v=>v.GetString())) {
            var file=_files.FirstOrDefault(f=>f.Fid==id&&!f.IsDir)??throw new ArgumentException("请选择文件。");
            DownloadLink link;
            if(_shareUrl==null)link=await client.ResolveDriveFileAsync(id);
            else link=(await PanelAdapter.For(client).GetShareDownloadAsync(new[]{id},new[]{file.Token},_shareId,_shareToken)).First();
            links.Add(new LinkExporter.Item{Url=link.DownloadUrl,OutputPath=file.Name});
        }
        var c=client.BuildDownloadContext();var format=S(p,"format") switch{"curl"=>LinkExporter.Format.Curl,"aria2"=>LinkExporter.Format.Aria2,"labeled"=>LinkExporter.Format.Labeled,"motrix"=>LinkExporter.Format.MotrixGui,_=>LinkExporter.Format.PlainUrls};
        if(!string.IsNullOrEmpty(c.Authorization))throw new InvalidOperationException("WebDAV 链接需要账号授权，请使用应用内下载，避免导出密码。");
        return new{text=LinkExporter.Build(links,c.UserAgent,c.Cookie,c.Referer,format)};
    }
}
