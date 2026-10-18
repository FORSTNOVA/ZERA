using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ZERA.Core.Download;

internal static class Program
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string Body = "# list\nudp://tracker.example:80/announce\nhttps://tracker.example/announce\ninvalid\n";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Redirect;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body) };
            if (Redirect != null) response.Headers.Location = new Uri(Redirect);
            return Task.FromResult(response);
        }
    }
    private static int Main(string[] args)
    { try { Run(args).GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    private static async Task Run(string[] args)
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tracker-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Check(TrackerStore.SubscriptionUrl("https://github.com/ngosang/trackerslist/blob/master/trackers_best.txt") ==
            "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt", "GitHub file page normalization");
        try { TrackerStore.SubscriptionUrl("https://github.com/ngosang/trackerslist"); throw new Exception("Repository homepage accepted"); } catch (ArgumentException) { }
        try { TrackerStore.SubscriptionUrl("http://example.com/list"); throw new Exception("HTTP subscription accepted"); } catch (ArgumentException) { }
        try { TrackerStore.Parse("file:///bad", true); throw new Exception("Invalid manual tracker accepted"); } catch (ArgumentException) { }
        Check(TrackerStore.Parse("\uFEFFudp://a.example:80/announce,https://b.example/announce\nudp://a.example:80/announce", true).Count == 2, "BOM, aria2 comma format and deduplication");
        var store = new TrackerStore(root);
        var settings = store.Load();
        settings.ManualText = "udp://tracker.example:80/announce\nhttp://manual.example/announce";
        settings.Subscriptions.Add(new TrackerSubscription { Name = "自定义 GitHub 名称", Url = "https://example.com/list" });
        settings.Subscriptions.Add(new TrackerSubscription { Name = "停用源", Url = "https://example.com/disabled", Enabled = false });
        store.Save(settings);
        using (var handler = new Handler())
        using (var http = new HttpClient(handler))
        {
            settings = await store.UpdateAsync(false, http);
            Check(handler.Calls == 1 && settings.Effective.Count == 3, "Enabled sources only, merge/dedup");
            Check(settings.Subscriptions[0].LastSuccessUtc != default, "Success timestamp");
            await store.UpdateAsync(false, http);
            Check(handler.Calls == 1, "Daily refresh throttled");
            handler.Status = HttpStatusCode.Forbidden;
            settings = await store.UpdateAsync(true, http);
            Check(settings.Effective.Count == 3 && settings.Subscriptions[0].Error.Contains("403"), "HTTP error preserves cache");
            await store.UpdateAsync(false, http);
            Check(handler.Calls == 2, "Failed auto refresh backed off");
            handler.Status = HttpStatusCode.OK; handler.Body = "<html>wrong page</html>";
            settings = await store.UpdateAsync(true, http);
            Check(settings.Effective.Count == 3 && settings.Subscriptions[0].Error.Length > 0, "Invalid body preserves cache");
            handler.Body = new string('x', 1024 * 1024 + 1);
            settings = await store.UpdateAsync(true, http);
            Check(settings.Effective.Count == 3 && settings.Subscriptions[0].Error.Contains("1 MiB"), "Oversize body rejected");
            handler.Status = HttpStatusCode.Redirect; handler.Redirect = "http://example.com/unsafe";
            settings = await store.UpdateAsync(true, http);
            Check(settings.Effective.Count == 3 && settings.Subscriptions[0].Error.Length > 0, "HTTPS downgrade rejected");
            handler.Redirect = null; handler.Status = HttpStatusCode.OK; handler.Body = "https://new.example/announce";
            settings = await store.UpdateAsync(true, http);
            Check(settings.Effective.Contains(handler.Body) && !settings.Effective.Contains("https://tracker.example/announce"), "Successful refresh replaces stale entries");
            settings.AutoUpdate = false; store.Save(settings);
            int calls = handler.Calls; await store.UpdateAsync(false, http);
            Check(handler.Calls == calls, "Auto refresh disabled");
        }
        var restart = new TrackerStore(root).Load();
        Check(restart.Subscriptions[0].Name == "自定义 GitHub 名称" && restart.Subscriptions[0].Trackers.Count == 1 && !restart.AutoUpdate, "Restart retains name/link/cache/preferences");
        restart.Subscriptions[0].Name = "已改名"; restart.Subscriptions.RemoveAt(1); store.Save(restart);
        Check(store.Load().Subscriptions.Single().Name == "已改名", "Rename and delete persistence");
        Console.WriteLine("PASS: subscription CRUD, persistence, GitHub URL conversion, cache, daily/manual refresh, limits and failures");
        if (args.Contains("--live"))
        {
            var live = new TrackerStore(Path.Combine(root, "github"));
            var value = live.Load();
            value.Subscriptions.Add(new TrackerSubscription { Name = "ngosang", Url = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt" });
            value.Subscriptions.Add(new TrackerSubscription { Name = "XIU2", Url = "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt" });
            live.Save(value); value = await live.UpdateAsync(true);
            foreach (var sub in value.Subscriptions)
            { Check(sub.Error.Length == 0 && sub.Trackers.Count > 0, "Live GitHub subscription " + sub.Name + ": " + sub.Error); Console.WriteLine("PASS: live GitHub " + sub.Name + " " + sub.Trackers.Count + " trackers"); }
        }
        string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Runtime");
        foreach (bool split in new[] { false, true })
        foreach (bool isPrivate in new[] { false, true })
        using (var swarm = new LocalSwarm(1024 * 1024 + 127, "tracker-test.bin", false, isPrivate, false) { DelayMs = 1 })
        {
            // The torrent has NO embedded tracker. Only the configured list can find this seed.
            string test = Path.Combine(root, (split ? "split" : "aria") + (isPrivate ? "-private" : "-public"));
            Directory.CreateDirectory(test);
            string torrent = Path.Combine(test, "fixture.torrent"); File.WriteAllBytes(torrent, swarm.Torrent);
            Func<System.Collections.Generic.List<string>> provider = () => new System.Collections.Generic.List<string> { swarm.TrackerUrl };
            string gid;
            using (var aria = split ? null : new TorrentEngine(Path.Combine(runtime, "aria2", "aria2c.exe"), Path.Combine(test, "state")) { TrackerProvider = provider })
            using (var engine = split ? new SplitTorrentEngine(runtime, Path.Combine(test, "state")) { TrackerProvider = provider } : null)
            {
                gid = split ? await engine.AddAsync(torrent, test, 2) : await aria.AddTorrentAsync(torrent, test);
                var end = DateTime.UtcNow.AddSeconds(isPrivate ? 3 : 45); TorrentStatus done = null;
                do
                {
                    var tasks = split ? engine.GetTasks() : await aria.GetTasksAsync();
                    done = tasks.FirstOrDefault(t => t.Gid == gid);
                    if (done?.Status == "error") throw new Exception(done.Error);
                    if (done?.Status == "complete") break;
                    await Task.Delay(100);
                } while (DateTime.UtcNow < end);
                if (isPrivate) Check(swarm.Transferred == 0 && swarm.TrackerRequests == 0, "Private torrent ignores public trackers");
                else
                {
                    Check(done?.Status == "complete", "Extra tracker discovers seed (" + (split ? "split" : "aria") + ")");
                    Check(File.ReadAllBytes(Path.Combine(done.Directory, swarm.Name)).SequenceEqual(swarm.Payload), "Exact bytes using configured tracker");
                }
                Console.WriteLine("PASS: " + (split ? "libtorrent split" : "aria2") + (isPrivate ? " private torrent excludes extra trackers" : " public tracker-only discovery and exact transfer"));
            }
        }
    }
}
