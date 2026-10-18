using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Download;

internal static class Program
{
    private const int Total = 2 * 1024 * 1024;
    private const int Part = 512 * 1024;
    private const string Secret = "test-cookie-secret-0123456789";
    private static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        "test-data-" + Guid.NewGuid().ToString("N"));

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--live")
        {
            try { LiveDownload.Run(args[1]).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine("Live diagnostic failed: " + ex.GetType().Name); Environment.Exit(1); }
            return 0;
        }
        Directory.CreateDirectory(Root);
        try
        {
            RejectionKeepsProgress().GetAwaiter().GetResult();
            if (args.Contains("--rejection-only")) return 0;
            TransientFailureRecovers().GetAwaiter().GetResult();
            BoundedErrorBodies().GetAwaiter().GetResult();
            SubrequestsPreservePlan(WriteMode.SharedFile).GetAwaiter().GetResult();
            SubrequestsPreservePlan(WriteMode.SegmentedFiles).GetAwaiter().GetResult();
            SubrequestsPreservePlan(WriteMode.SharedFile, true).GetAwaiter().GetResult();
            InvalidRangesNeverWrite().GetAwaiter().GetResult();
            IdleAndPauseHandling().GetAwaiter().GetResult();
            RestartedBaiduResolvesPath().GetAwaiter().GetResult();
            Console.WriteLine("PASS: all download regression checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static SegmentedDownloader Engine(HttpMessageHandler handler, string fallback = null,
        long maxRequestBytes = 0, WriteMode mode = WriteMode.SharedFile)
    {
        var engine = new SegmentedDownloader(2, "native-test", "http://localhost/", "BDUSS=" + Secret + "; flag=1",
            mode, null, "http://localhost", fallback, maxRequestBytes: maxRequestBytes);
        var field = typeof(SegmentedDownloader).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        ((HttpClient)field.GetValue(engine)).Dispose();
        field.SetValue(engine, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        return engine;
    }

    private static byte[] Bytes(long start, int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++) result[i] = (byte)((start + i) % 251);
        return result;
    }

    private static HttpResponseMessage RangeResponse(HttpRequestMessage request)
    {
        var range = request.Headers.Range.Ranges.Single();
        long from = range.From.Value;
        long to = range.To ?? Total - 1;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(Bytes(from, (int)(to - from + 1))),
            RequestMessage = request
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Total);
        return response;
    }

    private static void SeedProgress(string dest)
    {
        using (var stream = File.Create(dest))
        {
            stream.SetLength(Total);
            var bytes = Bytes(0, Part);
            stream.Write(bytes, 0, bytes.Length);
        }
        var rows = new List<string> { Total.ToString(), "4" };
        for (int i = 0; i < 4; i++) rows.Add((i * Part) + "," + ((i + 1) * Part - 1) + "," + (i == 0 ? Part : 0));
        File.WriteAllLines(dest + ".qmeta", rows);
    }

    private static async Task RejectionKeepsProgress()
    {
        string dest = Path.Combine(Root, "rejected.bin");
        SeedProgress(dest);
        var logs = new List<string>();
        int rejected = 0, fallbackRequests = 0;
        using (var engine = Engine(new Handler(request =>
        {
            if (request.Headers.Range != null && request.Headers.Range.ToString() == "bytes=0-0")
                return RangeResponse(request);
            Interlocked.Increment(ref rejected);
            bool browser = request.Headers.UserAgent.ToString().Contains("browser-test");
            if (browser) Interlocked.Increment(ref fallbackRequests);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(browser
                    ? "{\"error_code\":31362,\"error_msg\":\"sign error\"}"
                    : "{\"error_code\":31326,\"error_msg\":\"user is not authorized, hitcode:104 "
                      + Secret + " https://localhost/file?sign=private-signed-url\"}")
            };
        }), "browser-test"))
        using (var timeout = new CancellationTokenSource(12000))
        {
            engine.Log = line => { lock (logs) logs.Add(line); };
            var sw = Stopwatch.StartNew();
            var result = await engine.DownloadAsync("http://localhost/download", dest, cancel: timeout.Token);
            string text = result.Message + string.Join("\n", logs);
            Assert(!result.Ok, "rejection incorrectly succeeded");
            Assert(sw.Elapsed.TotalSeconds < 6, "rejection waited for stall timeout");
            Assert(result.Message.Contains("HTTP 403") && result.Message.Contains("31362"), "final error lost HTTP/server code");
            Assert(text.Contains("31326") && text.Contains("hitcode:104"), "initial rejection diagnostics lost");
            Assert(!text.Contains(Secret) && !text.Contains("private-signed-url"), "diagnostics leaked credentials");
            Assert(fallbackRequests > 0 && rejected <= 4, "UA fallback missing or rejected parts requeued");
            Assert(File.Exists(dest + ".qmeta"), "resume metadata lost");
            using (var file = File.OpenRead(dest))
            {
                var prefix = new byte[Part];
                Assert(file.Read(prefix, 0, Part) == Part && prefix.SequenceEqual(Bytes(0, Part)), "existing downloaded bytes changed");
            }
            Console.WriteLine("PASS: prompt 403, original/server errors, UA fallback, redaction, preserved progress ("
                + sw.ElapsedMilliseconds + " ms, " + rejected + " rejected requests)");
        }
    }

    private static async Task TransientFailureRecovers()
    {
        int rejected = 0;
        string dest = Path.Combine(Root, "recovered.bin");
        using (var engine = Engine(new Handler(request =>
        {
            if (request.Headers.Range.ToString() != "bytes=0-0" && Interlocked.Increment(ref rejected) == 1)
                return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"errno\":429}") };
            return RangeResponse(request);
        })))
        using (var timeout = new CancellationTokenSource(12000))
        {
            var result = await engine.DownloadAsync("http://localhost/download", dest, cancel: timeout.Token);
            Assert(result.Ok, "transient 429 stopped retrying: " + result.Message);
            Assert(File.ReadAllBytes(dest).SequenceEqual(Bytes(0, Total)), "recovered download contents differ");
            Assert(!File.Exists(dest + ".qmeta"), "completed metadata not removed");
            Console.WriteLine("PASS: 429 retries recover and final file contents match");
        }
    }

    private static async Task BoundedErrorBodies()
    {
        using (var engine = Engine(new Handler(RangeResponse)))
        {
            var describe = typeof(SegmentedDownloader).GetMethod("DescribeHttpFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var body in new HttpContent[] {
                new StringContent("<html>" + Secret + "</html>"),
                new StringContent(new string('x', 10000)),
                new StreamContent(new NeverReadStream()) })
            using (var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = body })
            {
                var sw = Stopwatch.StartNew();
                var task = (Task<string>)describe.Invoke(engine, new object[] { response, CancellationToken.None });
                var result = await task;
                Assert(result == "HTTP 403", "unparseable/error body leaked or masked HTTP status");
                Assert(sw.Elapsed.TotalSeconds < 4, "error body read is not bounded");
            }
            Console.WriteLine("PASS: malformed, oversized and stalled error bodies retain HTTP status without leaking body");
        }
    }

    private static async Task SubrequestsPreservePlan(WriteMode mode, bool throwDuringRead = false)
    {
        const int prefix = 12345, cap = 65536;
            string dest = Path.Combine(Root, "subrequests-" + mode + (throwDuringRead ? "-io" : "") + ".bin");
        SeedProgress(dest);
        var plan = File.ReadAllLines(dest + ".qmeta");
        plan[2] = "0," + (Part - 1) + "," + prefix;
        File.WriteAllLines(dest + ".qmeta", plan);
        if (mode == WriteMode.SegmentedFiles)
        {
            File.WriteAllBytes(dest, Bytes(0, prefix));
            Directory.CreateDirectory(dest + ".qparts");
            File.WriteAllBytes(Path.Combine(dest + ".qparts", "p000000"), Bytes(0, prefix));
        }
        var ranges = new List<Tuple<long, long>>();
        int truncated = 0;
        using (var engine = Engine(new Handler(request =>
        {
            var r = request.Headers.Range.Ranges.Single();
            if (r.From == 0 && r.To == 0) return RangeResponse(request);
            long start = r.From.Value, end = r.To.Value;
            Assert(end - start + 1 <= cap, "request exceeded cap");
            Assert(start >= prefix, "resume re-requested completed prefix");
            lock (ranges) ranges.Add(Tuple.Create(start, end));
            var response = RangeResponse(request);
            // Declare the requested length but end one response early. Resume must
            // account for every byte already written and reset retry count on success.
            if (start == prefix && Interlocked.Increment(ref truncated) == 1)
            {
                response.Content = throwDuringRead
                    ? (HttpContent)new StreamContent(new ThrowAfterPrefixStream(Bytes(start, 1000)))
                    : new ByteArrayContent(Bytes(start, 1000));
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Total);
                response.Content.Headers.ContentLength = end - start + 1;
            }
            return response;
        }), maxRequestBytes: cap, mode: mode))
        using (var timeout = new CancellationTokenSource(20000))
        {
            var result = await engine.DownloadAsync("http://localhost/download", dest, cancel: timeout.Token);
            Assert(result.Ok, "bounded requests failed: " + result.Message);
            Assert(File.ReadAllBytes(dest).SequenceEqual(Bytes(0, Total)), "bounded/resumed contents differ");
            Assert(ranges.Any(r => r.Item1 == prefix + 1000), "short body did not resume at actual written offset");
            Assert(ranges.Count > 16, "test did not exercise many successful subrequests");
            Assert(!File.Exists(dest + ".qmeta"), "completed sidecar retained");
            Assert(!Directory.Exists(dest + ".qparts"), "completed fragments retained");
            Console.WriteLine("PASS: " + mode + " old plan/unaligned resume, bounded subrequests, "
                + (throwDuringRead ? "read exception" : "truncated body") + " recovery and exact final contents");
        }
    }

    private static async Task InvalidRangesNeverWrite()
    {
        foreach (string fault in new[] { "start", "end", "total", "length", "ignored" })
        {
            string dest = Path.Combine(Root, "invalid-" + fault + ".bin");
            SeedProgress(dest);
            byte[] before = File.ReadAllBytes(dest);
            using (var engine = Engine(new Handler(request =>
            {
                var r = request.Headers.Range.Ranges.Single();
                if (r.From == 0 && r.To == 0) return RangeResponse(request);
                var response = RangeResponse(request);
                if (fault == "start") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(r.From.Value + 1, r.To.Value, Total);
                if (fault == "end") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(r.From.Value, r.To.Value + 1, Total);
                if (fault == "total") response.Content.Headers.ContentRange = new ContentRangeHeaderValue(r.From.Value, r.To.Value, Total + 1);
                if (fault == "length") response.Content.Headers.ContentLength = 99;
                if (fault == "ignored") response.StatusCode = HttpStatusCode.OK;
                return response;
            }), maxRequestBytes: 65536))
            using (var timeout = new CancellationTokenSource(5000))
            {
                var result = await engine.DownloadAsync("http://localhost/download", dest, cancel: timeout.Token);
                Assert(!result.Ok, "invalid response succeeded: " + fault);
                Assert(File.ReadAllBytes(dest).SequenceEqual(before), "invalid response wrote bytes: " + fault);
                Assert(File.Exists(dest + ".qmeta"), "invalid response erased resume data");
            }
        }
        Console.WriteLine("PASS: wrong Content-Range/length and ignored Range never write data");
    }

    private static async Task IdleAndPauseHandling()
    {
        using (var engine = Engine(new Handler(RangeResponse)))
        {
            var method = typeof(SegmentedDownloader).GetMethod("WaitForNetworkAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(int));
            using (var cancel = new CancellationTokenSource())
            {
                var pending = new TaskCompletionSource<int>();
                bool aborted = false;
                var task = (Task<int>)method.Invoke(engine, new object[] { pending.Task, cancel, null, "test read", (Action)(() => aborted = true), 150 });
                try { await task; throw new Exception("idle did not time out"); }
                catch (TimeoutException) { }
                Assert(aborted && cancel.IsCancellationRequested, "idle did not abort actual request");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var pause = new PauseToken();
                pause.Pause();
                var pending = new TaskCompletionSource<int>();
                var task = (Task<int>)method.Invoke(engine, new object[] { pending.Task, cancel, pause, "test paused read", (Action)(() => { }), 150 });
                await Task.Delay(400);
                Assert(!task.IsCompleted, "pause counted toward idle timeout");
                pause.Resume();
                pending.SetResult(7);
                Assert(await task == 7, "paused request did not resume");
            }
            using (var cancel = new CancellationTokenSource())
            {
                bool aborted = false;
                var task = (Task<int>)method.Invoke(engine, new object[] { new TaskCompletionSource<int>().Task, cancel, null, "test cancelled read", (Action)(() => aborted = true), 5000 });
                cancel.Cancel();
                try { await task; throw new Exception("cancel did not interrupt read"); }
                catch (OperationCanceledException) { }
                Assert(aborted, "cancellation did not close response");
            }
        }
        Console.WriteLine("PASS: idle aborts uncooperative reads, pause excludes idle time, cancellation closes response");
    }

    private static async Task RestartedBaiduResolvesPath()
    {
        foreach (bool wrongId in new[] { false, true })
        using (var client = new ZERA.Core.Api.BaiduClient("BDUSS=test-cookie"))
        {
            int metadataCalls = 0, nativeCalls = 0;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ZERA.Core.Api.BaiduClient).GetField("_bdstoken", flags).SetValue(client, "test-token");
            var session = typeof(ZERA.Core.Api.BaiduClient).GetField("_session", flags).GetValue(client);
            var httpField = session.GetType().GetField("_http", flags);
            ((HttpClient)httpField.GetValue(session)).Dispose();
            httpField.SetValue(session, new HttpClient(new Handler(request => {
                string body;
                if (request.RequestUri.AbsolutePath == "/api/filemetas") {
                    Assert(request.RequestUri.Query.Contains("fsids="), "file metadata must use fsids, not path target");
                    metadataCalls++;
                    body = "{\"errno\":0,\"info\":[{\"fs_id\":" + (wrongId ? "999" : "123") + ",\"path\":\"/test.bin\"}]}";
                }
                else {
                    nativeCalls++;
                    Assert(request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("test.bin"), "metadata path not used for link resolution");
                    body = "{\"errno\":0,\"urls\":[{\"url\":\"https://localhost/file\"}]}";
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            })));
            if (wrongId) {
                try { await client.ResolveDriveFileAsync("123"); throw new Exception("wrong metadata ID was accepted"); }
                catch (ZERA.Core.Panels.PanelApiException) { }
                Assert(nativeCalls == 0, "resolved another file's metadata");
            }
            else {
                Assert((await client.ResolveDriveFileAsync("123")).DownloadUrl == "https://localhost/file", "fresh client could not resolve path");
                await client.ResolveDriveFileAsync("123");
                Assert(metadataCalls == 1 && nativeCalls == 2, "path cache did not survive within client");
                Assert(client.BuildDownloadContext().MaxRequestBytes == 1048576, "Baidu cap not set");
            }
        }
        Console.WriteLine("PASS: restarted Baidu resolves exact file metadata, caches path, rejects mismatched IDs and supplies request cap");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            => Task.FromResult(respond(request));
    }

    private sealed class ThrowAfterPrefixStream : MemoryStream
    {
        public ThrowAfterPrefixStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel)
        {
            if (Position >= Length) throw new IOException("simulated connection reset after partial data");
            return base.ReadAsync(buffer, offset, count, cancel);
        }
    }

    private sealed class NeverReadStream : Stream
    {
        private readonly TaskCompletionSource<int> pending = new TaskCompletionSource<int>();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] b, int o, int n, CancellationToken c) => pending.Task;
        protected override void Dispose(bool disposing) { pending.TrySetCanceled(); base.Dispose(disposing); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
}
