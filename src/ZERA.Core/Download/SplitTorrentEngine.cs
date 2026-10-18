using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZERA.Core.Download
{
    /// <summary>Independent, disjoint piece-range workers; the Python coordinator owns native sessions.</summary>
    public sealed class SplitTorrentEngine : IDisposable
    {
        public sealed class Range
        {
            public int Index { get; set; }
            public int StartPiece { get; set; }
            public int EndPiece { get; set; }
            public long Total { get; set; }
            public long Completed { get; set; }
            public string Status { get; set; }
            public long DownloadSpeed { get; set; }
            public long UploadSpeed { get; set; }
            public long Connections { get; set; }
        }
        public sealed class Plan
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string TorrentSha256 { get; set; }
            public long Total { get; set; }
            public int Workers { get; set; }
            public List<Range> Ranges { get; set; }
            public string Error { get; set; }
        }
        public sealed class Snapshot
        {
            public string Name { get; set; }
            public string Status { get; set; }
            public long Completed { get; set; }
            public string Error { get; set; }
            public List<Range> Workers { get; set; }
        }
        public sealed class Control
        {
            public bool[] Paused { get; set; }
            public bool Stop { get; set; }
            public List<string> Trackers { get; set; }
        }
        private readonly string _python, _helper, _stateDirectory;
        private readonly FileStream _lock;
        private readonly List<string> _jobs;
        private readonly Dictionary<string, Process> _processes = new Dictionary<string, Process>();
        private readonly object _sync = new object();
        private bool _disposed;
        public Func<List<string>> TrackerProvider { get; set; }

        public SplitTorrentEngine(string runtimeDirectory, string stateDirectory)
        {
            _python = Path.Combine(runtimeDirectory, "win-x64", "python.exe");
            _helper = Path.Combine(runtimeDirectory, "bt-split", "split_download.py");
            _stateDirectory = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(_stateDirectory);
            _lock = new FileStream(Path.Combine(_stateDirectory, "registry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { _jobs = Read<List<string>>(Path.Combine(_stateDirectory, "jobs.json")) ?? new List<string>(); }
            catch { _lock.Dispose(); throw; }
        }

        public async Task<string> AddAsync(string torrentPath, string directory, int workers)
        {
            ThrowIfDisposed();
            var file = new FileInfo(torrentPath);
            if (!file.Exists || !file.Extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase) || file.Length > 8 * 1024 * 1024 || workers < 2 || workers > 8)
                throw new ArgumentException("请选择不超过 8 MiB 的 BT v1 种子，以及 2 至 8 个子任务。");
            CheckOtherGroup(null);
            string root = Path.Combine(Path.GetFullPath(directory), "BT-Split-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(root);
            File.Copy(file.FullName, Path.Combine(root, "source.torrent"));
            Plan plan = await Task.Run(() => Inspect(Path.Combine(root, "source.torrent"), workers)).ConfigureAwait(false);
            plan.Id = Guid.NewGuid().ToString("N");
            Write(Path.Combine(root, "job.json"), plan);
            Write(Path.Combine(root, "control.json"), new Control { Paused = new bool[plan.Workers],
                Trackers = TrackerStore.Parse(string.Join("\n", TrackerProvider?.Invoke() ?? new List<string>()), true) });
            lock (_sync)
            {
                ThrowIfDisposed();
                CheckOtherGroup(null);
                _jobs.Add(root);
                SaveRegistry();
                Start(root);
            }
            return "split:" + plan.Id;
        }

        private Plan Inspect(string torrentPath, int workers)
        {
            var start = StartInfo("--inspect " + Quote(torrentPath) + " " + workers);
            using (var process = Process.Start(start))
            {
                // Only bounded inspection JSON is emitted. Native stderr is never surfaced as tracker text.
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException("拆分引擎解析种子超时。"); }
                var result = Json().Deserialize<Plan>(output.GetAwaiter().GetResult());
                error.GetAwaiter().GetResult();
                if (process.ExitCode != 0 || result == null || result.Ranges == null || result.Workers < 2)
                    throw new IOException(result?.Error ?? "种子不支持拆分，或分块引擎不可用。");
                return result;
            }
        }

        private ProcessStartInfo StartInfo(string arguments)
        {
            if (!File.Exists(_python) || !File.Exists(_helper)) throw new FileNotFoundException("缺少分块下载引擎，请完整解压新版程序。");
            return new ProcessStartInfo(_python, "-I -S " + Quote(_helper) + " " + arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(_helper),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
        }
        private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024 };
        private static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return Json().Deserialize<T>(reader.ReadToEnd());
        }
        private static void Write(string path, object value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json().Serialize(value), new UTF8Encoding(false));
            for (int retry = 0; ; retry++)
            {
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); return; }
                catch (IOException) when (retry < 10) { Thread.Sleep(20); }
            }
        }
        private void SaveRegistry() { lock (_sync) Write(Path.Combine(_stateDirectory, "jobs.json"), _jobs); }
        private bool Running(string root) { lock (_sync) return _processes.TryGetValue(root, out var process) && !process.HasExited; }
        private void CheckOtherGroup(string root)
        {
            lock (_sync) if (_processes.Any(pair => pair.Key != root && !pair.Value.HasExited))
                throw new IOException("当前已有一组独立分块任务，请先暂停该整组任务，再启动另一组。");
        }
        private void Start(string root)
        {
            lock (_sync)
            {
            ThrowIfDisposed();
            if (Running(root)) return;
            CheckOtherGroup(root);
            if (_processes.TryGetValue(root, out var old)) old.Dispose();
            var process = Process.Start(StartInfo(Quote(root) + " " + Process.GetCurrentProcess().Id));
            process.OutputDataReceived += (s, e) => { };
            process.ErrorDataReceived += (s, e) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            _processes[root] = process;
            }
        }

        public List<TorrentStatus> GetTasks()
        {
            lock (_sync) return ReadTasks();
        }

        private List<TorrentStatus> ReadTasks()
        {
            ThrowIfDisposed();
            var result = new List<TorrentStatus>();
            foreach (string root in _jobs.ToArray())
            {
                var plan = Read<Plan>(Path.Combine(root, "job.json"));
                if (plan == null) continue;
                var snapshot = Read<Snapshot>(Path.Combine(root, "status.json"));
                var entries = snapshot?.Workers?.Count == plan.Workers ? snapshot.Workers : plan.Ranges;
                bool running = Running(root);
                string state = snapshot?.Status ?? (running ? "active" : "paused");
                if (!running && state != "complete" && state != "error") state = "paused";
                if (!running && snapshot == null && _processes.ContainsKey(root)) state = "error";
                if (!running && state != "complete" && _processes.TryGetValue(root, out var exited) && exited.ExitCode != 0 &&
                    Read<Control>(Path.Combine(root, "control.json"))?.Stop != true) state = "error";
                string error = snapshot?.Error ?? "";
                if (state == "error" && error.Length == 0) error = "分块进程启动或恢复失败，保留分片；请检查引擎、目录和原始种子是否被修改，再点击整组继续。";
                string gid = "split:" + plan.Id;
                result.Add(new TorrentStatus {
                    Gid = gid, Name = "[分组] " + plan.Name, Status = state, Total = plan.Total,
                    Completed = snapshot?.Completed ?? 0, Split = true, Group = true,
                    Directory = state == "complete" ? Path.Combine(root, "result") : root, Error = error,
                    DownloadSpeed = running ? entries.Sum(e => e.DownloadSpeed) : 0,
                    UploadSpeed = running ? entries.Sum(e => e.UploadSpeed) : 0,
                    Connections = running ? entries.Sum(e => e.Connections) : 0
                });
                foreach (var entry in entries)
                    result.Add(new TorrentStatus {
                        Gid = gid + ":" + entry.Index, Name = "  子任务 " + (entry.Index + 1) + " · 块 " + entry.StartPiece + "–" + (entry.EndPiece - 1),
                        Status = entry.Status == "complete" ? "complete" : running ? entry.Status ?? "active" : "paused",
                        Total = entry.Total, Completed = entry.Completed, Split = true, Group = false, Error = "",
                        Directory = Path.Combine(root, "worker-" + entry.Index.ToString("D2")),
                        DownloadSpeed = running ? entry.DownloadSpeed : 0, UploadSpeed = running ? entry.UploadSpeed : 0,
                        Connections = running ? entry.Connections : 0
                    });
            }
            return result;
        }

        private string Find(string gid, out int? worker)
        {
            string[] parts = gid.Split(':');
            if (parts.Length < 2 || parts.Length > 3 || parts[0] != "split") throw new ArgumentException("无效的分组任务。");
            worker = parts.Length == 3 ? int.Parse(parts[2]) : (int?)null;
            string root;
            lock (_sync) root = _jobs.Find(path => Read<Plan>(Path.Combine(path, "job.json"))?.Id == parts[1]);
            if (root == null) throw new IOException("分组任务已不存在。");
            return root;
        }

        public async Task PauseAsync(string gid)
        {
            ThrowIfDisposed();
            string root = Find(gid, out var worker);
            bool allPaused;
            lock (_sync)
            {
                var control = Read<Control>(Path.Combine(root, "control.json"));
                if (worker.HasValue) control.Paused[worker.Value] = true;
                else for (int i = 0; i < control.Paused.Length; i++) control.Paused[i] = true;
                Write(Path.Combine(root, "control.json"), control);
                allPaused = control.Paused.All(value => value);
            }
            if (allPaused) await Task.Run(() => Stop(root)).ConfigureAwait(false);
        }
        public Task ResumeAsync(string gid)
        {
            lock (_sync)
            {
            ThrowIfDisposed();
            string root = Find(gid, out var worker);
            CheckOtherGroup(root);
            var control = Read<Control>(Path.Combine(root, "control.json"));
            control.Stop = false;
            // On process restart, other unfinished children remain paused when resuming one child.
            if (!Running(root)) for (int i = 0; i < control.Paused.Length; i++) control.Paused[i] = true;
            if (worker.HasValue) control.Paused[worker.Value] = false;
            else for (int i = 0; i < control.Paused.Length; i++) control.Paused[i] = false;
            Write(Path.Combine(root, "control.json"), control);
            Start(root);
            return Task.CompletedTask;
            }
        }
        public async Task RemoveAsync(string gid)
        {
            ThrowIfDisposed();
            string root = Find(gid, out var worker);
            if (worker.HasValue) throw new IOException("请选中分组行移除整组；单独移除子任务会使最终文件缺块。");
            await Task.Run(() => Stop(root)).ConfigureAwait(false);
            lock (_sync) { _jobs.Remove(root); SaveRegistry(); }
        }
        private void Stop(string root)
        {
            Process process;
            lock (_sync)
            {
            var control = Read<Control>(Path.Combine(root, "control.json"));
            if (control != null)
            {
                control.Stop = true;
                for (int i = 0; i < control.Paused.Length; i++) control.Paused[i] = true;
                Write(Path.Combine(root, "control.json"), control);
            }
            _processes.TryGetValue(root, out process);
            }
            if (process != null)
            {
                if (!process.HasExited && !process.WaitForExit(8000)) { process.Kill(); process.WaitForExit(2000); }
            }
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SplitTorrentEngine)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            string[] roots;
            lock (_sync) roots = _processes.Keys.ToArray();
            try { foreach (string root in roots) Stop(root); }
            finally { foreach (var process in _processes.Values) process.Dispose(); _lock.Dispose(); }
        }
    }
}
