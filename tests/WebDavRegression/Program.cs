using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Download;
using ZERA.Core.Panels;

internal static class Program
{
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    private static int Main() { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    private static async Task Run()
    {
        Check((int)PanelKind.Quark == 0 && (int)PanelKind.Baidu == 1 && (int)PanelKind.WebDav == 2, "Stable persisted enum values");
        Check(!PanelRegistry.Describe(PanelKind.WebDav).SupportsShare && PanelRegistry.Describe(PanelKind.Quark).SupportsTransfer, "Capability registry");
        var scope = new Uri("https://example.com/dav/");
        foreach (string invalid in new[] { "https://evil.test/dav/file", "http://example.com/dav/file", "https://example.com/outside/file", "https://example.com/dav/%252e%252e/file" })
            Check(!ScopedAuthorizationHandler.InScope(scope, new Uri(invalid)), "Credential scope rejection");
        try { new WebDavAccount { Endpoint = "http://example.com/dav" }.Serialize(); throw new Exception("HTTP accepted without opt-in"); } catch (ArgumentException) { }
        using (var server = new DavFixture())
        {
            string credential = new WebDavAccount { Endpoint = server.Root, Username = "user", Password = "SECRET-PASS", Name = "测试服务", AllowHttp = true }.Serialize();
            using (var client = PanelRegistry.Create(PanelKind.WebDav, credential))
            {
                var login = await PanelAdapter.For(client).LoginAsync();
                Check(login.Valid && login.Name == "测试服务" && login.Key.StartsWith("dav_"), "Unified login and stable server/account identity");
                var entries = await client.ListDirAsync("0", 40);
                Check(entries.Count == 2 && entries.Any(f => f.Name == "中文 #.bin" && f.Size == server.Data.Length) && entries.Any(f => f.IsDir), "Namespaced multistatus, Unicode/space/hash, only direct children and successful properties");
                var quota = await client.GetCapacityAsync(); Check(quota.total == 100 && quota.used == 10, "Optional quota properties");
                var file = entries.First(e => !e.IsDir); var link = await client.ResolveDriveFileAsync(file.Fid); var ctx = client.BuildDownloadContext();
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dav-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "download.bin");
                Func<SegmentedDownloader> engine = () => new SegmentedDownloader(threads: 2, ua: ctx.UserAgent, referer: ctx.Referer, origin: ctx.Origin,
                    cookie: ctx.Cookie, authorization: ctx.Authorization, credentialScope: ctx.CredentialScope);
                using (var dl = engine())
                using (var error = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"errmsg\":\"SECRET-PASS Basic dXNlcjpTRUNSRVQtUEFTUw==\"}") })
                {
                    var method = typeof(SegmentedDownloader).GetMethod("DescribeHttpFailureAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Check(await (Task<string>)method.Invoke(dl, new object[] { error, CancellationToken.None }) == "HTTP 403", "WebDAV server error cannot echo credentials into download logs");
                }
                using (var cancel = new CancellationTokenSource(180))
                using (var dl = engine())
                {
                    var interrupted = await dl.DownloadAsync(link.DownloadUrl, target, cancel: cancel.Token, pause: new PauseToken());
                    Check(!interrupted.Ok && server.Sent > 0 && server.Sent < server.Data.Length, "Cancellation interrupts a partially transferred file");
                }
                using (var dl = engine())
                {
                    var result = await dl.DownloadAsync(link.DownloadUrl, target, pause: new PauseToken());
                    Check(result.Ok, "Authenticated download / resume: " + result.Message);
                }
                Check(File.ReadAllBytes(target).SequenceEqual(server.Data) && server.NonzeroRanges > 0, "Exact Range transfer and nonzero resume");
                Check(server.Sent < server.Data.Length + 512 * 1024, "Restart reuses saved ranges rather than redownloading the entire payload");
                Check(server.BadHeaders == 0 && server.Authorized > 3, "Every request authenticated without cloud headers/cookies");
                Check(await client.RefreshLinkAsync(new PendingInfo { Fid = file.Fid }) == file.Fid, "Restart identifier refresh without embedded credential");
                server.Mode = "redirect";
                try { await client.ValidateAsync(); throw new Exception("Redirect accepted"); } catch (PanelApiException ex) { Check(ex.Status == 302, "Redirect not followed with credential"); }
                server.Mode = "html";
                try { await client.ValidateAsync(); throw new Exception("HTML accepted"); } catch (PanelApiException) { }
                server.Mode = "xml";
                try { await client.ListDirAsync("0", 1); throw new Exception("DTD accepted"); } catch (System.Xml.XmlException) { }
            }
            server.Mode = "ok";
            using (var wrong = new WebDavClient(new WebDavAccount { Endpoint = server.Root, Username = "user", Password = "bad", AllowHttp = true }.Serialize()))
            {
                try { await wrong.ValidateAsync(); throw new Exception("Bad password accepted"); } catch (PanelApiException ex) { Check(ex.Status == 401 && !ex.Message.Contains("SECRET"), "Auth error is sanitized"); }
            }
            server.Mode = "anonymous";
            using (var anonymous = new WebDavClient(new WebDavAccount { Endpoint = server.Root, AllowHttp = true }.Serialize()))
                Check((await anonymous.ValidateAsync()).ok && anonymous.BuildDownloadContext().Authorization == null, "Anonymous WebDAV service");
        }
        Console.WriteLine("PASS: unified registry/login, Basic auth, PROPFIND/XML/status, Unicode paths, quota and directory scope");
        Console.WriteLine("PASS: real authenticated Range download, cancellation/restart resume and exact bytes, no cloud headers");
        Console.WriteLine("PASS: HTTP opt-in, credential scope, redirects, HTML/DTD rejection and authentication errors");
    }
}
