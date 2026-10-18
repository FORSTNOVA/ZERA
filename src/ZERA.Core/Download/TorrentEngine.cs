using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZERA.Core.Download
{
    public sealed class TorrentStatus
    {
        public string Gid { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Directory { get; set; }
        public string Error { get; set; }
        public long Total { get; set; }
        public long Completed { get; set; }
        public long DownloadSpeed { get; set; }
        public long UploadSpeed { get; set; }
        public long Connections { get; set; }
        public bool Metadata { get; set; }
        public bool Split { get; set; }
        public bool Group { get; set; }
        public double Percent => Total > 0 ? Math.Min(100, Completed * 100.0 / Total) : 0;
    }

    /// <summary>Owns one bundled aria2 process. RPC stays on loopback with a random secret.</summary>
    public sealed class TorrentEngine : IDisposable
    {
        private readonly string _executable, _stateDirectory;
        private readonly SemaphoreSlim _startGate = new SemaphoreSlim(1, 1);
        private readonly HttpClient _http = new HttpClient(new HttpClientHandler { UseProxy = false }) {
            Timeout = TimeSpan.FromSeconds(5) };
        private Process _process;
        private FileStream _stateLock;
        private Uri _endpoint;
        private string _secret;
        private bool _disposed;
        public Func<List<string>> TrackerProvider { get; set; }

        public TorrentEngine(string executable, string stateDirectory)
        {
            _executable = Path.GetFullPath(executable);
            _stateDirectory = Path.GetFullPath(stateDirectory);
        }

        public async Task StartAsync()
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TorrentEngine));
                if (_process != null && !_process.HasExited) return;
                if (!File.Exists(_executable)) throw new FileNotFoundException("缺少 BT 引擎，请完整解压新版程序。", _executable);
                _process?.Dispose();
                _stateLock?.Dispose();
                Directory.CreateDirectory(_stateDirectory);
                try { _stateLock = new FileStream(Path.Combine(_stateDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { throw new IOException("种子任务目录已被其他下载引擎使用，请先关闭旧程序。"); }
                var session = Path.Combine(_stateDirectory, "session.txt");
                if (!File.Exists(session)) File.WriteAllText(session, "", new UTF8Encoding(false));
                byte[] secret = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
                _secret = Convert.ToBase64String(secret);
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                _endpoint = new Uri("http://127.0.0.1:" + port + "/jsonrpc");
                string args = "--no-conf=true --enable-rpc=true --rpc-listen-all=false --rpc-allow-origin-all=false" +
                    " --rpc-listen-port=" + port + " --rpc-secret=" + _secret +
                    " --rpc-max-request-size=16M --rpc-save-upload-metadata=true --pause=true --pause-metadata=false" +
                    " --seed-time=0 --max-concurrent-downloads=2 --bt-max-peers=40 --max-overall-upload-limit=256K" +
                    " --enable-dht=true --enable-dht6=false --bt-enable-lpd=false --enable-peer-exchange=true" +
                    " --check-integrity=true --auto-file-renaming=false --allow-overwrite=false --file-allocation=none" +
                    " --save-session-interval=5 --auto-save-interval=5 --save-not-found=true" +
                    " --console-log-level=error --summary-interval=0 --download-result=hide" +
                    " --stop-with-process=" + Process.GetCurrentProcess().Id +
                    " --input-file=" + Quote(session) + " --save-session=" + Quote(session) +
                    " --dht-file-path=" + Quote(Path.Combine(_stateDirectory, "dht.dat"));
                _process = Process.Start(new ProcessStartInfo(_executable, args) {
                    WorkingDirectory = _stateDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true });
                // Drain both pipes; raw engine output can contain private tracker URLs.
                _process.OutputDataReceived += (s, e) => { };
                _process.ErrorDataReceived += (s, e) => { };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                for (int i = 0; i < 40; i++)
                {
                    if (_process.HasExited) throw new IOException("BT 引擎启动失败，退出代码：" + _process.ExitCode);
                    try
                    {
                        await RpcAsync("getVersion").ConfigureAwait(false);
                        await RpcAsync("forcePauseAll").ConfigureAwait(false);
                        return;
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(100).ConfigureAwait(false);
                }
                throw new IOException("BT 引擎未响应，请检查本机安全软件是否阻止 aria2c.exe。");
            }
            catch
            {
                try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
                _process?.Dispose(); _process = null;
                _stateLock?.Dispose(); _stateLock = null;
                throw;
            }
            finally { _startGate.Release(); }
        }

        private static string Quote(string value)
            => "\"" + value.Replace("\"", "") + "\"";

        public static string ValidateMagnet(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length > 32768 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请粘贴有效的 magnet: 磁力链接。");
            bool valid = uri.Query.TrimStart('?').Split('&').Any(part => {
                int eq = part.IndexOf('=');
                if (eq < 0 || !part.Substring(0, eq).Equals("xt", StringComparison.OrdinalIgnoreCase)) return false;
                string xt = Uri.UnescapeDataString(part.Substring(eq + 1));
                const string prefix = "urn:btih:";
                if (!xt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                string hash = xt.Substring(prefix.Length);
                return hash.Length == 40 && hash.All(Uri.IsHexDigit) ||
                    hash.Length == 32 && hash.ToUpperInvariant().All(c => c >= 'A' && c <= 'Z' || c >= '2' && c <= '7');
            });
            if (!valid) throw new ArgumentException("磁力链接需要包含有效的 BT v1 信息哈希（xt=urn:btih:…）；暂不支持仅 BT v2 的链接。");
            return value;
        }

        public async Task<string> AddMagnetAsync(string magnet, string directory)
        {
            magnet = ValidateMagnet(magnet);
            return await AddAsync("addUri", new object[] { new[] { magnet } }, directory, false).ConfigureAwait(false);
        }

        public async Task<string> AddTorrentAsync(string path, string directory)
        {
            var file = new FileInfo(path);
            if (!file.Extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase) || !file.Exists ||
                file.Length == 0 || file.Length > 8 * 1024 * 1024)
                throw new ArgumentException("请选择有效的 .torrent 文件（不超过 8 MiB）。");
            byte[] bytes = File.ReadAllBytes(file.FullName);
            bool isPrivate = TorrentPrivacy.IsPrivate(bytes);
            return await AddAsync("addTorrent", new object[] { Convert.ToBase64String(bytes), new string[0] }, directory, isPrivate).ConfigureAwait(false);
        }

        private async Task<string> AddAsync(string method, object[] parameters, string directory, bool isPrivate)
        {
            directory = Path.GetFullPath(directory);
            if (directory.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("下载目录含无效字符。");
            await StartAsync().ConfigureAwait(false);
            // Isolate every import so two torrents with identical filenames cannot write each other's files.
            string taskDirectory = Path.Combine(directory, "BT-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(taskDirectory);
            var options = new Dictionary<string, string> { ["dir"] = taskDirectory, ["pause"] = "false" };
            var trackers = isPrivate ? new List<string>() : TrackerProvider?.Invoke() ?? new List<string>();
            options["bt-tracker"] = string.Join(",", TrackerStore.Parse(string.Join("\n", trackers), true));
            var args = parameters.Concat(new object[] { options }).ToArray();
            try
            {
                string gid = Convert.ToString(await RpcAsync(method, args).ConfigureAwait(false), CultureInfo.InvariantCulture);
                await RpcAsync("saveSession").ConfigureAwait(false);
                return gid;
            }
            catch
            {
                try { if (!Directory.EnumerateFileSystemEntries(taskDirectory).Any()) Directory.Delete(taskDirectory); } catch { }
                throw;
            }
        }

        public async Task<List<TorrentStatus>> GetTasksAsync()
        {
            await StartAsync().ConfigureAwait(false);
            var keys = new[] { "gid", "status", "totalLength", "completedLength", "downloadSpeed", "uploadSpeed",
                "connections", "dir", "errorCode", "bittorrent", "files", "followedBy", "infoHash", "verifiedLength", "verifyIntegrityPending" };
            var all = new List<Dictionary<string, object>>();
            foreach (var response in new[] {
                await RpcAsync("tellActive", new object[] { keys }).ConfigureAwait(false),
                await RpcAsync("tellWaiting", 0, 1000, keys).ConfigureAwait(false),
                await RpcAsync("tellStopped", 0, 1000, keys).ConfigureAwait(false) })
                all.AddRange(((object[])response).OfType<Dictionary<string, object>>());
            var result = new List<TorrentStatus>();
            foreach (var item in all.GroupBy(x => Text(x, "gid")).Select(x => x.First()))
            {
                if (item.TryGetValue("followedBy", out var followed) && followed is object[] f && f.Length > 0) continue;
                string name = "";
                if (item.TryGetValue("bittorrent", out var bt) && bt is Dictionary<string, object> b &&
                    b.TryGetValue("info", out var info) && info is Dictionary<string, object> i) name = Text(i, "name");
                bool metadata = string.IsNullOrEmpty(name) && Number(item, "totalLength") == 0;
                if (string.IsNullOrEmpty(name) && item.TryGetValue("files", out var files) && files is object[] paths && paths.Length > 0)
                    name = Path.GetFileName(Text((Dictionary<string, object>)paths[0], "path"));
                string status = Text(item, "status");
                if (string.IsNullOrEmpty(name)) name = metadata ?
                    (status == "paused" ? "BT 任务 · 待读取种子信息" : "磁力任务 · 等待元数据") : Text(item, "gid");
                if (status == "removed") continue;
                result.Add(new TorrentStatus {
                    Gid = Text(item, "gid"), Name = name, Status = status, Metadata = metadata,
                    Directory = Text(item, "dir"), Total = Number(item, "totalLength"), Completed = Number(item, "completedLength"),
                    DownloadSpeed = Number(item, "downloadSpeed"), UploadSpeed = Number(item, "uploadSpeed"), Connections = Number(item, "connections"),
                    Error = status == "error" ? "BT 下载失败，aria2 错误代码 " + Text(item, "errorCode") + "；检查目录、空间和种源后重新添加。" : ""
                });
            }
            return result;
        }

        public async Task PauseAsync(string gid)
        {
            await RpcAsync("forcePause", gid).ConfigureAwait(false);
            await RpcAsync("saveSession").ConfigureAwait(false);
        }
        public async Task ResumeAsync(string gid)
        {
            await RpcAsync("unpause", gid).ConfigureAwait(false);
            await RpcAsync("saveSession").ConfigureAwait(false);
        }
        public async Task RemoveAsync(string gid, bool stopped)
        {
            await RpcAsync(stopped ? "removeDownloadResult" : "forceRemove", gid).ConfigureAwait(false);
            await RpcAsync("saveSession").ConfigureAwait(false);
        }

        private async Task<object> RpcAsync(string method, params object[] args)
        {
            var parameters = new object[] { "token:" + _secret }.Concat(args).ToArray();
            var json = new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024 };
            string body = json.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "aria2." + method, @params = parameters });
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(_endpoint, content).ConfigureAwait(false))
            {
                string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var data = json.DeserializeObject(raw) as Dictionary<string, object>;
                if (data != null && data.TryGetValue("error", out var error) && error is Dictionary<string, object> e)
                    throw new IOException("BT 引擎拒绝操作（" + method + "，代码 " + Text(e, "code") + "）。种子可能无效、任务重复或状态已变化，请刷新列表。" );
                response.EnsureSuccessStatusCode();
                if (data == null || !data.TryGetValue("result", out var result)) throw new IOException("BT 引擎返回无效响应。");
                return result;
            }
        }

        private static string Text(Dictionary<string, object> value, string key)
            => value.TryGetValue(key, out var item) ? Convert.ToString(item, CultureInfo.InvariantCulture) : "";
        private static long Number(Dictionary<string, object> value, string key)
            => long.TryParse(Text(value, key), out var result) ? result : 0;

        public void Dispose()
        {
            _startGate.Wait();
            try
            {
                if (_disposed) return;
                _disposed = true;
                if (_process != null && !_process.HasExited)
                {
                    try { RpcAsync("forcePauseAll").GetAwaiter().GetResult(); RpcAsync("saveSession").GetAwaiter().GetResult(); } catch { }
                    try { RpcAsync("shutdown").GetAwaiter().GetResult(); } catch { }
                    if (!_process.WaitForExit(5000)) _process.Kill();
                }
            }
            finally
            {
                _process?.Dispose();
                _stateLock?.Dispose();
                _http.Dispose();
                _startGate.Release();
            }
        }
    }
}
