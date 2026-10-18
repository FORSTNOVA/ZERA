using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using ZERA.Core.Api;
using ZERA.Core.Download;
using ZERA.Core.Support;

// Explicit opt-in integration check. Credentials and signed links stay in memory.
internal static class LiveDownload
{
    public static async Task Run(string stage)
    {
        if (stage != "first" && stage != "resume" && stage != "resume-final" && stage != "metadata") throw new ArgumentException("Unknown stage");
        const string root = @"D:\ZERA\artifacts\fix2-live-test";
        const string target = "Adobe Acrobat Pro 2026.rar";
        Directory.CreateDirectory(root);
        string dest = Path.Combine(root, "target.rar");
        string meta = dest + ".qmeta";
        if (stage == "first" && File.Exists(meta)) throw new Exception("First-stage data already exists");
        if (stage != "first" && !File.Exists(meta)) throw new Exception("No resume metadata");
        var xml = new XmlDocument();
        xml.Load(@"D:\ZERA\artifacts\20261016\ZERA-v2.2.0-win64\ZERA.config.xml");
        string cookie = SecretProtector.Unprotect(xml.SelectSingleNode("/AppSettings/PanelCookies/PanelCookieEntry[Panel='Baidu']/Cookie").InnerText);
        NetworkConfig.Apply(64);
        var client = new BaiduClient(cookie);
        var request = typeof(BaiduClient).GetMethod("RequestJsonAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var query = new Dictionary<string, string> { { "key", target }, { "recursion", "1" }, { "num", "20" }, { "page", "1" } };
        var search = (Task<Dictionary<string, object>>)request.Invoke(client, new object[] {
            System.Net.Http.HttpMethod.Get, "https://pan.baidu.com/api/search", query, null, 20, CancellationToken.None });
        var data = await search;
        var matches = ((object[])data["list"]).Cast<Dictionary<string, object>>()
            .Where(f => (string)f["server_filename"] == target).ToList();
        if (matches.Count != 1) throw new Exception("File search not unique");
        var file = matches[0];
        string fid = Convert.ToString(file["fs_id"]);
        ZERA.Core.Models.DownloadLink link;
        try { link = await client.ResolveDriveFileAsync(fid); }
        catch (ZERA.Core.Panels.PanelApiException ex)
        {
            string safe = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"https?://\S+", "[url]");
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"[A-Za-z0-9_+/=\-]{24,}", "[redacted]");
            Console.Error.WriteLine("Metadata/link failure: " + safe.Substring(0, Math.Min(250, safe.Length)));
            throw;
        }
        var uri = new Uri(link.DownloadUrl);
        if (!(uri.Host.EndsWith(".baidupcs.com") || uri.Host.EndsWith(".baidu.com"))) throw new Exception("Unexpected host");
        var context = client.BuildDownloadContext();
        if (stage == "metadata")
        {
            string metadataReport = new JavaScriptSerializer().Serialize(new { MetadataRecovered = true, RequestCap = context.MaxRequestBytes, Host = uri.Host });
            File.WriteAllText(Path.Combine(root, "metadata.json"), metadataReport);
            Console.WriteLine(metadataReport);
            return;
        }
        long start = File.Exists(meta) ? File.ReadAllLines(meta).Skip(2).Sum(s => long.Parse(s.Split(',')[2])) : 0;
        var pause = new PauseToken();
        var reached = new TaskCompletionSource<bool>();
        var sw = Stopwatch.StartNew();
        long lastLog = -10000, latest = start;
        var progress = new InlineProgress(p => {
            latest = p.Done;
            if (sw.ElapsedMilliseconds - lastLog >= 10000) {
                lastLog = sw.ElapsedMilliseconds;
                Console.WriteLine("live " + stage + ": " + (p.Done - start) + " additional bytes; " + (long)p.Speed + " B/s");
            }
            long targetMiB = stage == "resume-final" ? 2 : stage == "resume" ? 9 : 8;
            if (p.Done >= start + targetMiB * 1024 * 1024) { pause.Pause(); reached.TrySetResult(true); }
        });
        var engine = new SegmentedDownloader(2, context.UserAgent, context.Referer, context.Cookie,
            WriteMode.SharedFile, null, context.Origin, maxRequestBytes: context.MaxRequestBytes);
        var logs = new List<string>();
        engine.Log = line => { lock (logs) logs.Add(line); };
        var download = engine.DownloadAsync(link.DownloadUrl, dest, progress, pause: pause);
        var winner = await Task.WhenAny(reached.Task, download, Task.Delay(240000));
        pause.Pause();
        await Task.Delay(2200); // Let the regular sidecar reporter persist safely flushed progress.
        var rows = File.Exists(meta) ? File.ReadAllLines(meta) : new string[0];
        long saved = rows.Length > 2 ? rows.Skip(2).Sum(s => long.Parse(s.Split(',')[2])) : 0;
        string prefixHash = "", offsetHash = "";
        if (File.Exists(dest)) using (var stream = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            var bytes = new byte[65536];
            if (stream.Read(bytes, 0, bytes.Length) == bytes.Length) using (var sha = SHA256.Create())
                prefixHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            stream.Position = 8370955;
            if (stream.Read(bytes, 0, bytes.Length) == bytes.Length) using (var sha = SHA256.Create())
                offsetHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        var report = new Dictionary<string, object> {
            { "Stage", stage }, { "ReachedTarget", winner == reached.Task }, { "StartSavedBytes", start },
            { "SavedBytes", saved }, { "ReportedBytes", latest }, { "Seconds", sw.Elapsed.TotalSeconds },
            { "LogicalParts", rows.Length > 1 ? int.Parse(rows[1]) : 0 }, { "RequestCap", context.MaxRequestBytes },
            { "Prefix64kSHA256", prefixHash }, { "Nonzero64kSHA256", offsetHash }, { "ProgressAdvanced", saved > start },
            { "Failure", download.IsCompleted ? (await download).Message : "" }
        };
        lock (logs) report["Logs"] = logs.ToArray();
        string json = new JavaScriptSerializer().Serialize(report);
        File.WriteAllText(Path.Combine(root, stage + ".json"), json);
        Console.WriteLine(json);
        // Simulate application exit/crash after a saved pause. Only this diagnostic
        // process exits; the user's download and its sidecar are never opened for writing.
        Environment.Exit(winner == reached.Task && saved > start ? 0 : 1);
    }

    private sealed class InlineProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> action;
        public InlineProgress(Action<DownloadProgress> action) { this.action = action; }
        public void Report(DownloadProgress p) { action(p); }
    }
}
