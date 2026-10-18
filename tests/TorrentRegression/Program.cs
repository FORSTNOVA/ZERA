using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZERA.Core.Download;

internal static class Program
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task<TorrentStatus> Wait(TorrentEngine engine, Func<TorrentStatus, bool> predicate, int seconds = 40)
    {
        var stop = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < stop)
        {
            var tasks = await engine.GetTasksAsync();
            var result = tasks.FirstOrDefault(predicate);
            if (result != null) return result;
            var error = tasks.FirstOrDefault(t => t.Status == "error");
            if (error != null) throw new Exception(error.Error);
            await Task.Delay(100);
        }
        throw new Exception("Timed out waiting for local BT fixture.");
    }
    private static int Main()
    {
        try { Run().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }
    private static async Task Run()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bt-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Runtime", "aria2", "aria2c.exe");
        string state = Path.Combine(root, "state");
        try { TorrentEngine.ValidateMagnet("https://example.com"); throw new Exception("Bad magnet accepted"); } catch (ArgumentException) { }
        try { TorrentEngine.ValidateMagnet("magnet:?xt=urn:btih:no"); throw new Exception("Bad hash accepted"); } catch (ArgumentException) { }
        Check(TorrentEngine.ValidateMagnet("magnet:?xt=urn:btih:" + new string('A', 32)).Length > 0, "Base32 magnet");
        using (var swarm = new LocalSwarm(4 * 1024 * 1024, "测试文件.bin"))
        {
            string torrent = Path.Combine(root, "test.torrent");
            File.WriteAllBytes(torrent, swarm.Torrent);
            string gid, directory;
            long before;
            using (var engine = new TorrentEngine(executable, state))
            {
                await engine.StartAsync();
                Check((await engine.GetTasksAsync()).Count == 0, "Clean startup");
                using (var duplicate = new TorrentEngine(executable, state))
                {
                    try { await duplicate.StartAsync(); throw new Exception("Concurrent engine acquired same state"); } catch (IOException) { }
                }
                string invalid = Path.Combine(root, "invalid.torrent"); File.WriteAllText(invalid, "not bencode");
                try { await engine.AddTorrentAsync(invalid, root); throw new Exception("Invalid torrent accepted"); } catch (IOException) { }
                gid = await engine.AddTorrentAsync(torrent, root);
                var active = await Wait(engine, t => t.Gid == gid && t.Completed > 0 && t.Completed < t.Total);
                directory = active.Directory;
                await engine.PauseAsync(gid);
                var paused = await Wait(engine, t => t.Gid == gid && t.Status == "paused");
                before = paused.Completed;
                await Task.Delay(300);
                Check((await engine.GetTasksAsync()).Single(t => t.Gid == gid).Completed == before, "Pause stops transfer");
                Console.WriteLine("PASS: real private BT peer transfer, invalid input, state locking, pause");
            }
            using (var engine = new TorrentEngine(executable, state))
            {
                await engine.StartAsync();
                var restored = await Wait(engine, t => t.Gid == gid && t.Status == "paused");
                Check(restored.Directory == directory, "Restart keeps task directory");
                Check(File.Exists(Path.Combine(directory, swarm.Name + ".aria2")), "Restart preserves BT control file");
                Check(new FileInfo(Path.Combine(directory, swarm.Name)).Length >= before, "Restart preserves downloaded bytes");
                await engine.ResumeAsync(gid);
                var complete = await Wait(engine, t => t.Gid == gid && t.Status == "complete");
                Check(File.ReadAllBytes(Path.Combine(complete.Directory, swarm.Name)).SequenceEqual(swarm.Payload), "BT final content exact");
                Check(swarm.Transferred < swarm.Payload.Length + 4 * 65536, "Resume reuses pieces rather than redownloading whole file");
                Check(complete.UploadSpeed == 0, "No seeding after completion");
                await engine.RemoveAsync(gid, true);
                Check(File.Exists(Path.Combine(directory, swarm.Name)), "Removing task keeps data");
                Console.WriteLine("PASS: engine restart restores paused task, resume, exact contents, stop seeding, remove preserves files");
            }
        }
        using (var swarm = new LocalSwarm(512 * 1024, "多文件", true) { DelayMs = 0 })
        using (var engine = new TorrentEngine(executable, Path.Combine(root, "magnet-state")))
        {
            string gid = await engine.AddMagnetAsync(swarm.Magnet, root);
            var complete = await Wait(engine, t => t.Status == "complete" && t.Name == swarm.Name, 60);
            byte[] result = File.ReadAllBytes(Path.Combine(complete.Directory, swarm.Name, "a.bin"))
                .Concat(File.ReadAllBytes(Path.Combine(complete.Directory, swarm.Name, "子目录", "b.bin"))).ToArray();
            Check(result.SequenceEqual(swarm.Payload), "Magnet metadata to multi-file exact contents");
            Console.WriteLine("PASS: magnet tracker lookup, BEP 10 metadata, multi-file Unicode paths, exact final contents");
        }
        string cancelState = Path.Combine(root, "cancel-state");
        string removedGid;
        using (var first = new LocalSwarm(512 * 1024, "same-name.bin") { DelayMs = 60 })
        using (var second = new LocalSwarm(256 * 1024, "same-name.bin") { DelayMs = 0 })
        using (var engine = new TorrentEngine(executable, cancelState))
        {
            string a = Path.Combine(root, "a.torrent"), b = Path.Combine(root, "b.torrent");
            File.WriteAllBytes(a, first.Torrent); File.WriteAllBytes(b, second.Torrent);
            removedGid = await engine.AddTorrentAsync(a, root);
            string otherGid = await engine.AddTorrentAsync(b, root);
            var active = await Wait(engine, t => t.Gid == removedGid && t.Completed > 0 && t.Status == "active");
            var other = await Wait(engine, t => t.Gid == otherGid && t.Status == "complete");
            Check(active.Directory != other.Directory, "Same-name torrents use independent directories");
            Check(File.ReadAllBytes(Path.Combine(other.Directory, second.Name)).SequenceEqual(second.Payload), "Parallel task exact contents");
            await engine.RemoveAsync(removedGid, false);
            for (int i = 0; i < 50 && (await engine.GetTasksAsync()).Any(t => t.Gid == removedGid); i++) await Task.Delay(100);
            Check(!(await engine.GetTasksAsync()).Any(t => t.Gid == removedGid), "Active remove stops task");
            Check(File.Exists(Path.Combine(active.Directory, first.Name)), "Active remove keeps partial file");
        }
        using (var engine = new TorrentEngine(executable, cancelState))
            Check(!(await engine.GetTasksAsync()).Any(t => t.Gid == removedGid), "Removed task is not restored after restart");
        Console.WriteLine("PASS: parallel same-name isolation, cancel active task, preserve partial file, no removed-task resurrection");
        Console.WriteLine("PASS: all torrent regression checks (loopback fixtures only)");
    }
}
