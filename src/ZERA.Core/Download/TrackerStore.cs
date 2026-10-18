using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZERA.Core.Download
{
    public sealed class TrackerSubscription
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public List<string> Trackers { get; set; } = new List<string>();
        public DateTime LastSuccessUtc { get; set; }
        public DateTime LastAttemptUtc { get; set; }
        public string Error { get; set; } = "";
        public string Summary => !Enabled ? "已停用" :
            (LastSuccessUtc == default ? "尚未更新" : Trackers.Count + " 条 · " + LastSuccessUtc.ToLocalTime().ToString("MM-dd HH:mm")) +
            (string.IsNullOrEmpty(Error) ? "" : " · " + Error);
    }

    public sealed class TrackerSettings
    {
        public string ManualText { get; set; } = "";
        public bool AutoUpdate { get; set; } = true;
        public List<TrackerSubscription> Subscriptions { get; set; } = new List<TrackerSubscription>();
        public List<string> Effective => TrackerStore.Parse(ManualText, true)
            .Concat(Subscriptions.Where(s => s.Enabled).SelectMany(s => s.Trackers))
            .Distinct(StringComparer.Ordinal).Take(1024).ToList();
    }

    /// <summary>Only plain tracker text is downloaded; failed refreshes keep the previous cache.</summary>
    public sealed class TrackerStore
    {
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _cacheLock = new object();
        private List<string> _cache;
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public TrackerStore(string stateDirectory) { _path = Path.Combine(stateDirectory, "trackers.json"); }
        public List<string> GetEffective()
        {
            lock (_cacheLock) if (_cache != null) return new List<string>(_cache);
            return Load().Effective;
        }
        private void Cache(TrackerSettings value) { lock (_cacheLock) _cache = value.Effective; }

        public static List<string> Parse(string text, bool strict)
        {
            if ((text ?? "").Length > 1024 * 1024) throw new ArgumentException("Tracker 列表不能超过 1 MiB。");
            var result = new List<string>();
            foreach (string item in (text ?? "").TrimStart('\uFEFF').Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = item.Trim();
                if (value.Length == 0 || value.StartsWith("#")) continue;
                bool valid = value.Length <= 2048 && !value.Any(char.IsWhiteSpace) &&
                    Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == "udp" || uri.Scheme == "http" || uri.Scheme == "https") &&
                    !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
                if (!valid) { if (strict) throw new ArgumentException("Tracker 格式无效：请使用 udp://、http:// 或 https:// 地址，每行一条。"); continue; }
                if (!result.Contains(value)) result.Add(value);
                if (result.Count > 1024) throw new ArgumentException("Tracker 列表最多支持 1024 条。");
            }
            return result;
        }

        public static string SubscriptionUrl(string value)
        {
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsoluteUri.Length > 2048)
                throw new ArgumentException("订阅需要 HTTPS 文本地址，或 GitHub 的文件页面链接。");
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                if (parts.Length < 5 || (parts[2] != "blob" && parts[2] != "raw"))
                    throw new ArgumentException("请填写 GitHub 具体列表文件的链接，而不是仓库首页。");
                return "https://raw.githubusercontent.com/" + parts[0] + "/" + parts[1] + "/" + string.Join("/", parts.Skip(3));
            }
            return uri.AbsoluteUri;
        }

        public TrackerSettings Load()
        {
            _gate.Wait();
            try { var value = Read(); Cache(value); return value; } finally { _gate.Release(); }
        }
        private TrackerSettings Read()
        {
            if (!File.Exists(_path)) return new TrackerSettings();
            try
            {
                var value = Json().Deserialize<TrackerSettings>(File.ReadAllText(_path, Encoding.UTF8));
                Validate(value);
                return value;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            { throw new IOException("Tracker 配置无法读取，请先备份 bt-state/trackers.json 后检查文件。", ex); }
        }
        private static void Validate(TrackerSettings value)
        {
            if (value == null || value.Subscriptions == null || value.Subscriptions.Count > 20)
                throw new ArgumentException("最多支持 20 个 Tracker 订阅。");
            value.ManualText = string.Join(Environment.NewLine, Parse(value.ManualText, true));
            var urls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sub in value.Subscriptions)
            {
                if (sub == null || string.IsNullOrWhiteSpace(sub.Name) || sub.Name.Trim().Length > 80)
                    throw new ArgumentException("请填写不超过 80 字的订阅名称。");
                sub.Name = sub.Name.Trim(); sub.Url = SubscriptionUrl(sub.Url);
                sub.Error = sub.Error ?? "";
                if (!urls.Add(sub.Url)) throw new ArgumentException("此订阅地址已存在。");
                sub.Trackers = Parse(string.Join("\n", sub.Trackers ?? new List<string>()), true);
            }
            if (value.Effective.Count == 1024 && Parse(value.ManualText, true).Concat(value.Subscriptions.Where(s => s.Enabled).SelectMany(s => s.Trackers)).Distinct().Count() > 1024)
                throw new ArgumentException("合并后的 Tracker 超过 1024 条，请减少订阅。");
        }
        public void Save(TrackerSettings value)
        {
            _gate.Wait();
            try { Validate(value); Write(value); } finally { _gate.Release(); }
        }
        private void Write(TrackerSettings value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + ".tmp";
            // Effective is derived and is never used as a second source of truth on load.
            File.WriteAllText(temp, Json().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temp, _path, null); else File.Move(temp, _path);
            Cache(value);
        }
        public Task<TrackerSettings> UpdateAsync(bool force) => UpdateAsync(force, null);
        public async Task<TrackerSettings> UpdateAsync(bool force, HttpClient client)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var value = Read();
                Cache(value);
                if (!force && !value.AutoUpdate) return value;
                bool owns = client == null;
                if (owns) client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
                try
                {
                    foreach (var sub in value.Subscriptions.Where(s => s.Enabled))
                    {
                        if (!force && DateTime.UtcNow - sub.LastAttemptUtc < TimeSpan.FromHours(sub.Error.Length > 0 ? 1 : 24)) continue;
                        sub.LastAttemptUtc = DateTime.UtcNow;
                        try
                        {
                            string body = await DownloadAsync(client, sub.Url).ConfigureAwait(false);
                            var trackers = Parse(body, false);
                            if (trackers.Count == 0) throw new IOException("未返回有效 Tracker，保留上次缓存");
                            var previous = sub.Trackers;
                            sub.Trackers = trackers;
                            if (value.Effective.Count >= 1024 && value.Subscriptions.Where(s => s.Enabled).SelectMany(s => s.Trackers).Concat(Parse(value.ManualText, true)).Distinct().Count() > 1024)
                            { sub.Trackers = previous; throw new IOException("合并超过 1024 条，保留上次缓存"); }
                            sub.LastSuccessUtc = DateTime.UtcNow; sub.Error = "";
                        }
                        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is ArgumentException)
                        { sub.Error = ex is IOException ? ex.Message : "获取失败，保留上次缓存"; }
                        Write(value); // Save each result, including the failure timestamp.
                    }
                }
                finally { if (owns) client.Dispose(); }
                return value;
            }
            finally { _gate.Release(); }
        }
        private static async Task<string> DownloadAsync(HttpClient client, string address)
        {
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, address))
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                    { address = SubscriptionUrl(new Uri(new Uri(address), response.Headers.Location).AbsoluteUri); continue; }
                    if (!response.IsSuccessStatusCode) throw new IOException("HTTP " + (int)response.StatusCode + "，保留上次缓存");
                    if (response.Content.Headers.ContentLength > 1024 * 1024) throw new IOException("列表超过 1 MiB，保留上次缓存");
                    using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancel.Token).ConfigureAwait(false)) > 0)
                        { if (output.Length + count > 1024 * 1024) throw new IOException("列表超过 1 MiB，保留上次缓存"); output.Write(buffer, 0, count); }
                        return Encoding.UTF8.GetString(output.ToArray());
                    }
                }
            }
            throw new IOException("重定向次数过多，保留上次缓存");
        }
    }
}
