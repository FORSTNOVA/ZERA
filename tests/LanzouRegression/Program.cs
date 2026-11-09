using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using ZERA.Core.Download;
using ZERA.Core.Panels;
using ZERA.Core.Support;

internal static class Program
{
    private static int _checks;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); _checks++; Console.WriteLine("PASS: " + name); }
    private static async Task Failure(Func<Task> action, string expected)
    {
        try { await action(); } catch (PanelApiException ex) { Check(ex.Message.Contains(expected), "Action reports " + expected); return; }
        throw new Exception("FAIL: expected " + expected);
    }
    private static int Main(string[] args)
    {
        try { if (args.Length == 2 && args[0] == "--live") Live(args[1]).GetAwaiter().GetResult(); else Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + _checks + " Lanzou checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Run()
    {
        var info = ShareUrlParser.Parse("文件 https://wwx.lanzoux.com/iPlain?pwd=abcd 提取码：abcd");
        Check(info.PwdId == "https://wwx.lanzoux.com/iPlain" && info.Passcode == "abcd", "Full share text extracts URL and query password");
        Check(ShareUrlParser.Parse("链接：https://wws.lanzoul.com/iOne 密码：Z9x8").Passcode == "Z9x8", "Chinese password label");
        foreach (string fake in new[] { "https://lanzoux.com.evil.com/iOne", "https://notlanzoux.com/iOne", "https://evil.com/?x=lanzoux.com", "https://lanzoux.com:8080/iOne", "https://user@lanzoux.com/iOne", "https://www.ilanzou.com/iOne" })
            Check(PanelRegistry.Detect(fake) != PanelKind.Lanzou, "Reject misleading share host " + fake);
        Check(PanelRegistry.All().Count() == 10 && !PanelRegistry.Describe(PanelKind.Lanzou).SupportsDrive && !PanelRegistry.Describe(PanelKind.Lanzou).SupportsQrLogin, "Registry capabilities and stable enum values");
        Check((int)PanelKind.Thunder == 8 && (int)PanelKind.Lanzou == 9, "Existing platform numbers preserved");
        using (var registered = PanelRegistry.Create(PanelKind.Lanzou, "SECRET_COOKIE")) Check(registered.CookieStr == "" && !registered.IsLoggedIn, "Share client discards account credentials");

        var server = new Server();
        using (var client = new LanzouClient(server))
        {
            var adapter = PanelAdapter.For(client);
            Check(await adapter.GetStokenAsync("https://wwx.lanzoux.com/iPlain") == "lanzou", "Existing share adapter opens public share");
            var files = await adapter.ShareDetailAsync(null, null, "0");
            Check(files.Count == 1 && files[0].Name == "测试 & 文件.zip" && files[0].Size == 1572864, "Single file name HTML decoding and size");
            var links = await adapter.GetShareDownloadAsync(new[] { files[0].Fid }, new[] { "lanzou" }, null, null);
            Check(links[0].DownloadUrl == "https://cdn.example.com/file.zip?token=1", "Iframe, form sign and gateway redirect produce file URL");
            Check(server.RefererChecked && server.FormChecked && server.GatewayCookieChecked, "Protocol referer/sign/download cookie");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iModern", "");
            Check((await client.ListDirAsync("0", 40)).Single().Name == "modern.apk" && server.ModernFormChecked && server.ChallengeChecked, "Modern official AJAX endpoint, kd=1 and browser cookie challenge");
            var pending = new PendingInfo { Panel = PanelKind.Lanzou, Fid = files[0].Fid, Dest = "test.zip", FromShare = true };
            string serialized = new JavaScriptSerializer().Serialize(pending);
            Check(new JavaScriptSerializer().Deserialize<PendingInfo>(serialized).CanResume, "Pending share survives serialization without transient share tokens");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iSecret", "good");
            var protectedFiles = await client.ListDirAsync("0", 40);
            Check(protectedFiles.Single().Name == "加密文件.zip", "Password response filename");
            using (var restarted = new LanzouClient(server.Clone()))
                Check((await restarted.ResolveDriveFileAsync(protectedFiles[0].Fid)).DownloadUrl.Contains("token=2"), "Restart restores original per-file password");
            server.Token = "fresh";
            Check((await client.RefreshLinkAsync(pending)).EndsWith("token=fresh"), "Switching shares does not change old task; refresh bypasses stale link");
            server.Token = "1";
            await client.OpenShareAsync("https://wwx.lanzoux.com/iSecret", "wrong");
            await Failure(async () => await client.ListDirAsync("0", 40), "提取码错误");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iSecret", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "需要提取码");
            await client.OpenShareAsync("https://wwx.lanzoux.com/bFolder", "folderpw");
            var folder = await client.ListDirAsync("0", 40);
            Check(folder.Count == 3 && folder.Count(f => f.IsDir) == 1 && folder.Any(f => f.Size == 2048), "Folder pagination, child folder and files");
            Check(server.FolderPages == 3 && server.FolderPasswordChecked, "Folder requests propagate password and exhaust pages");
            var nested = await client.ListDirAsync(folder.Single(f => f.IsDir).Fid, 40);
            Check(nested.Count == 0 && server.ChildPasswordCleared, "Child folder has independent empty password");
            var selected = await client.ResolveDriveFileAsync(folder.First(f => !f.IsDir).Fid);
            Check(selected.DownloadUrl.Contains("token=2"), "Folder file carries original folder password");
            await client.OpenShareAsync("https://wwx.lanzoux.com/bFolder", "bad");
            await Failure(async () => await client.ListDirAsync("0", 40), "提取码错误");
            await client.OpenShareAsync("https://wwx.lanzoux.com/bFolder", "folderpw");
            await Failure(async () => await client.ListDirAsync("0", 1), "列目录上限");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iGate", "");
            Check((await client.ListDirAsync("0", 40)).Count == 1 && server.SecondGatewayChecked, "Gateway verification form resolves via ajax.php");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iInvalid", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "分享已失效");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iCaptcha", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "安全验证");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iMalicious", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "不受信任");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iRedirect", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "不受信任");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iChanged", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "页面规则");
            await client.OpenShareAsync("https://wwx.lanzoux.com/iHttpError", "");
            await Failure(async () => await client.ListDirAsync("0", 40), "HTTP 403");
            Check(!server.UntrustedRequested, "Untrusted redirects and iframes never receive requests");
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel(); bool canceled = false;
                try { await client.ListDirAsync("0", 40, cts.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Cancellation preserved");
            }
            var context = client.BuildDownloadContext();
            Check(context.SuggestedThreads == 2 && context.MaxRequestBytes == 4194304 && string.IsNullOrEmpty(context.Authorization) && context.Cookie == "down_ip=1", "Download uses conservative ranges and no account authorization");
        }
    }
    private static async Task Live(string url)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using (var client = new LanzouClient())
        {
            var links = await client.ResolveShareAsync(url, "", 1);
            Check(links.Count == 1, "Live share resolves one file");
            var link = links[0];
            Console.WriteLine("File: " + link.Name + "; estimated size: " + link.Size + "; CDN host: " + new Uri(link.DownloadUrl).Host);
            var context = client.BuildDownloadContext();
            using (var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) })
            {
                foreach (long start in new long[] { 0, 8388608 })
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, link.DownloadUrl))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
                        request.Headers.TryAddWithoutValidation("Cookie", context.Cookie);
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, start + 65535);
                        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        {
                            Check(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == start, "Live Range accepted at offset " + start);
                            byte[] data = await response.Content.ReadAsByteArrayAsync();
                            Check(data.Length == 65536, "Live range received exactly 64 KiB");
                            if (start == 0) Check(data[0] == 'P' && data[1] == 'K', "APK response is ZIP binary rather than an HTML page");
                            Console.WriteLine("Range: " + response.Content.Headers.ContentRange);
                        }
                    }
                }
            }
        }
    }
    private sealed class Server : HttpMessageHandler
    {
        public string Token = "1";
        public bool RefererChecked, FormChecked, GatewayCookieChecked, FolderPasswordChecked, ChildPasswordCleared, SecondGatewayChecked, UntrustedRequested;
        public int FolderPages;
        public bool ModernFormChecked, ChallengeChecked;
        public Server Clone() => new Server();
        private static HttpResponseMessage Response(string text, string type = "text/html") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };
        private static HttpResponseMessage Redirect(string url) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(url); return r; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string path = request.RequestUri.AbsolutePath;
            if (!request.RequestUri.Host.EndsWith("lanzoux.com") && request.RequestUri.Host != "apifile.woozooo.com" && request.RequestUri.Host != "slsstm2.dmpdmp.com") { UntrustedRequested = true; throw new Exception("Unexpected host"); }
            var form = HttpUtility.ParseQueryString(request.Content == null ? "" : await request.Content.ReadAsStringAsync());
            switch (path)
            {
                case "/iPlain": return Response("<title>测试 &amp; 文件.zip - 蓝奏云</title><div class='fileinfo'>大小：1.5 MB</div><iframe src='/fn?file=11'></iframe>");
                case "/iGate": return Response("<title>gate.zip - 蓝奏云</title><iframe src='/fn?file=33'></iframe>");
                case "/iModern": return Response("<title>modern.apk - 蓝奏云</title><iframe src='/fnModern'></iframe>");
                case "/fnModern": return Response("var sg='MODERN';var key='WXYZ';var kdns=1;var kdns=0;var domain1='https://apifile.woozooo.com/ajaxfile.php?file=123';$.ajax({url:domain1,data:{'action':'downprocess','sign':sg,'websignkey':key,'signs':key,'websign':'','kd':kdns,'ves':1}});");
                case "/ajaxfile.php":
                    ModernFormChecked = form["kd"] == "1" && form["sign"] == "MODERN" && form["websignkey"] == "WXYZ" && request.Headers.Referrer.AbsolutePath == "/fnModern";
                    return Response("{\"zt\":1,\"dom\":\"https://slsstm2.dmpdmp.com\",\"url\":\"modern\"}", "text/json");
                case "/file/modern":
                    string cookie = request.Headers.TryGetValues("Cookie", out var challengeCookies) ? string.Join(";", challengeCookies) : "";
                    if (!cookie.Contains("acw_sc__v2=")) return Response("<script>var arg1='0000000000000000000000000000000000000000';</script>");
                    ChallengeChecked = cookie.Contains("acw_sc__v2=3000176000856006061501533003690027800375") && cookie.Contains("down_ip=1");
                    Check(ChallengeChecked, "Browser-cookie challenge matches fixed known vector");
                    return Redirect("https://cdn.example.com/modern.apk");
                case "/fn":
                    string n = HttpUtility.ParseQueryString(request.RequestUri.Query)["file"];
                    return Response("var real='SIGN';var sign=real; $.ajax({url:'/ajaxm.php?file=" + n + "',data:{'action':'downprocess', 'sign':sign,'ves':1}});");
                case "/iSecret": case "/iOther": return Response("<div id='pwdload'></div>大小：2 MB<script>function down_p(){var sg='PW-SIGN';$.ajax({url:'/ajaxm.php?file=22',data:{'action':'downprocess','sign':sg,'p':pwd,'kd':1}});}</script>");
                case "/ajaxm.php":
                    if (request.RequestUri.Query == "?file=22")
                    {
                        Check(form["sign"] == "PW-SIGN", "Password AJAX sign parameter");
                        if (form["p"] != "good" && form["p"] != "folderpw") return Response("{\"zt\":0,\"inf\":\"密码不正确\"}", "application/json");
                        return Response("{\"zt\":1,\"inf\":\"加密文件.zip\",\"dom\":\"https://download.lanzoux.com\",\"url\":\"two\"}", "application/json");
                    }
                    RefererChecked = request.Headers.Referrer?.AbsolutePath == "/fn";
                    FormChecked = form["action"] == "downprocess" && form["sign"] == "SIGN" && form["ves"] == "1";
                    return Response("{\"zt\":1,\"dom\":\"https://download.lanzoux.com\",\"url\":\"" + (request.RequestUri.Query == "?file=33" ? "gate" : "one") + "\"}", "application/json");
                case "/file/one": case "/file/two":
                    GatewayCookieChecked = request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Single() == "down_ip=1";
                    return Redirect("https://cdn.example.com/file.zip?token=" + (path.EndsWith("two") ? "2" : Token));
                case "/file/gate": return Response("var token='second';$.ajax({data:{'token':token,'el':'1'}});");
                case "/ajax.php": SecondGatewayChecked = form["el"] == "2" && form["token"] == "second"; return Response("{\"url\":\"https://cdn.example.com/gate.zip\"}", "application/json");
                case "/bFolder": return Response("<div id='infos'></div><a class='folderlink' href='/bChild'><span>子目录</span></a><script>var fid='123';var t='TOK';$.ajax({url:'/filemoreajax.php',data:{'lx':2,'fid':fid,'t':t,'k':'KEY','pg':pg,'pwd':pwd}});</script>");
                case "/bChild": return Response("<div id='infos'></div><script>$.ajax({data:{'fid':'456','t':'TOK','k':'KEY'}});</script>");
                case "/filemoreajax.php":
                    if (form["fid"] == "456") { ChildPasswordCleared = form["pwd"] == ""; return Response("{\"zt\":2,\"text\":[]}", "application/json"); }
                    if (form["pwd"] != "folderpw") return Response("{\"zt\":3,\"inf\":\"密码错误\"}", "application/json");
                    FolderPasswordChecked = form["t"] == "TOK" && form["k"] == "KEY"; FolderPages++;
                    if (form["pg"] == "3") return Response("{\"zt\":2,\"text\":[]}", "application/json");
                    return Response("{\"zt\":1,\"text\":[{\"id\":\"" + (form["pg"] == "1" ? "iSecret" : "iOther") + "\",\"name_all\":\"file.zip\",\"size\":\"2 K\"}]}", "application/json");
                case "/iInvalid": return Response("文件不存在，已取消分享");
                case "/iCaptcha": return Response("<script>var arg1='ABCD';</script>");
                case "/iMalicious": return Response("<iframe src='http://127.0.0.1/private'></iframe>");
                case "/iRedirect": return Redirect("https://evil.example.com/private");
                case "/iChanged": return Response("<h1>新的页面</h1>");
                case "/iHttpError": return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("SECRET_SERVER_BODY") };
                default: throw new Exception("Unexpected request " + request.RequestUri);
            }
        }
    }
}
