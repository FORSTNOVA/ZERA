using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZERA.Core.Download;

internal static class Program
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task<TorrentStatus> Wait(SplitTorrentEngine engine, Func<TorrentStatus, bool> condition, int seconds = 90)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var tasks = engine.GetTasks();
            var result = tasks.FirstOrDefault(condition);
            if (result != null) return result;
            var failure = tasks.FirstOrDefault(t => t.Status == "error");
            if (failure != null) throw new Exception(failure.Error + " (" + failure.Directory + ")");
            await Task.Delay(100);
        }
        throw new Exception("Timed out waiting for split fixture.");
    }
    private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
    private static async Task Run()
    {
        string runtime = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Runtime");
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "split-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string registry = Path.Combine(root, "state");
        using (var swarm = new LocalSwarm(8 * 1024 * 1024, "独立分块.bin") { DelayMs = 25 })
        {
            string torrent = Path.Combine(root, "fixture.torrent"); File.WriteAllBytes(torrent, swarm.Torrent);
            string gid;
            long saved;
            using (var engine = new SplitTorrentEngine(runtime, registry))
            {
                try { using (var other = new SplitTorrentEngine(runtime, registry)) { throw new Exception("Registry lock not enforced"); } }
                catch (IOException) { }
                gid = await engine.AddAsync(torrent, root, 4);
                var active = await Wait(engine, t => t.Gid == gid && t.Completed > 0 && t.Completed < t.Total);
                Check(engine.GetTasks().Count(t => t.Split && !t.Group) == 4, "Four independent worker rows");
                await engine.PauseAsync(gid + ":0");
                var child = await Wait(engine, t => t.Gid == gid + ":0" && t.Status == "paused");
                long childBefore = child.Completed;
                try { await engine.AddAsync(torrent, root, 2); throw new Exception("Concurrent split group unexpectedly allowed"); }
                catch (IOException) { }
                await Task.Delay(500);
                Check(engine.GetTasks().Single(t => t.Gid == gid + ":0").Completed == childBefore, "Independent child pause");
                await engine.PauseAsync(gid);
                var paused = await Wait(engine, t => t.Gid == gid && t.Status == "paused");
                saved = paused.Completed;
                Check(saved > 0 && saved < paused.Total, "Partial verified progress saved");
                string bitmap = Directory.GetFiles(paused.Directory, "verified.bits", SearchOption.AllDirectories)
                    .First(path => File.ReadAllBytes(path).Any(value => value != 0));
                byte[] bits = File.ReadAllBytes(bitmap);
                int localPiece = Enumerable.Range(0, bits.Length * 8).First(i => (bits[i / 8] & (1 << (i % 8))) != 0);
                using (var part = new FileStream(Path.Combine(Path.GetDirectoryName(bitmap), "data.part"), FileMode.Open, FileAccess.Write))
                { part.Position = localPiece * 65536; part.WriteByte(255); }
                Console.WriteLine("PASS: four independent range tasks, verified compact parts, child pause, group pause");
            }
            using (var engine = new SplitTorrentEngine(runtime, registry))
            {
                Check(engine.GetTasks().Single(t => t.Gid == gid).Status == "paused", "Restart is paused");
                string copiedTorrent = Path.Combine(engine.GetTasks().Single(t => t.Gid == gid).Directory, "source.torrent");
                File.AppendAllText(copiedTorrent, "\n");
                await engine.ResumeAsync(gid);
                await Wait(engine, t => t.Gid == gid && t.Status == "error");
                File.WriteAllBytes(copiedTorrent, swarm.Torrent);
                await engine.ResumeAsync(gid);
                var complete = await Wait(engine, t => t.Gid == gid && t.Status == "complete");
                Check(File.ReadAllBytes(Path.Combine(complete.Directory, swarm.Name)).SequenceEqual(swarm.Payload), "Merged bytes equal original");
                Check(engine.GetTasks().Where(t => !t.Group).All(t => t.Completed == t.Total), "All worker ranges completed");
                Check(swarm.Transferred < swarm.Payload.Length + 16 * 65536, "Resume avoids downloading entire data again");
                foreach (var connection in swarm.PieceRequests.Values.Where(v => v.Count > 0))
                    Check(connection.Select(piece => piece / 32).Distinct().Count() == 1, "Each independent connection requests only its assigned quarter");
                Check(swarm.PieceRequests.Values.Where(v => v.Count > 0).Select(v => v.First() / 32).Distinct().Count() == 4, "All four disjoint quarters requested");
                Console.WriteLine("PASS: restart resume, modified torrent rejected, corrupted compact-piece recovery, disjoint requests, exact merged contents");
                await engine.RemoveAsync(gid);
                Check(engine.GetTasks().Count == 0, "Remove group removes all rows");
                Check(File.Exists(Path.Combine(complete.Directory, swarm.Name)), "Remove group preserves final files");
            }
        }
        using (var swarm = new LocalSwarm(1024 * 1024 + 731, "跨文件合并", true) { DelayMs = 1 })
        using (var engine = new SplitTorrentEngine(runtime, Path.Combine(root, "multi-state")))
        {
            string torrent = Path.Combine(root, "multi.torrent"); File.WriteAllBytes(torrent, swarm.Torrent);
            string gid = await engine.AddAsync(torrent, root, 4);
            var done = await Wait(engine, t => t.Gid == gid && t.Status == "complete");
            byte[] result = File.ReadAllBytes(Path.Combine(done.Directory, swarm.Name, "a.bin"))
                .Concat(File.ReadAllBytes(Path.Combine(done.Directory, swarm.Name, "子目录", "b.bin"))).ToArray();
            Check(result.SequenceEqual(swarm.Payload), "Cross-file and final short-piece merge exact");
            Console.WriteLine("PASS: Unicode multi-file reconstruction across piece/file boundaries and short final piece");
        }
        Console.WriteLine("PASS: all split torrent integration checks (private loopback swarm)");
    }
}
