using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Panels;

static class Program
{
    private const string Secret = "SENSITIVE-TOKEN-DO-NOT-LOG";
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Main() { Run().GetAwaiter().GetResult(); }
    sealed class Fixture : HttpMessageHandler
    {
        public PanelKind Kind;
        public bool Unauthorized, AlwaysUnauthorized, Renewed, Error, Endless, Redirect, HostileRoute, Delay, MalformedList;
        public int Calls, RefreshCalls;
        public List<string> Requests = new List<string>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (Delay) await Task.Delay(30000, ct);
            string path = request.RequestUri.AbsolutePath; Requests.Add(path);
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
            bool refresh = path.Contains("refreshToken") || path.Contains("access_token") || path.EndsWith("auth/token");
            if (refresh)
            {
                RefreshCalls++; Renewed = true;
                Check(request.Headers.Authorization == null, Kind + " renewal excludes stale authorization");
                string payload = Kind == PanelKind.Pan123 ? "{\"code\":0,\"data\":{\"accessToken\":\"renewed\"}}" :
                    Kind == PanelKind.Pan115 ? "{\"state\":true,\"data\":{\"access_token\":\"renewed\",\"refresh_token\":\"rotated\"}}" : "{\"access_token\":\"renewed\",\"refresh_token\":\"rotated\"}";
                return Json(payload);
            }
            if (AlwaysUnauthorized || Unauthorized && !Renewed) return Json("{\"code\":401}", HttpStatusCode.Unauthorized);
            if (path.EndsWith("getSessionForPC.action"))
            {
                Check(request.Headers.Authorization == null && !request.Headers.Contains("SessionKey"), "Tianyi session exchange excludes stale session");
                return Json("{\"res_code\":0,\"sessionKey\":\"new-session\",\"sessionSecret\":\"new-secret\",\"loginName\":\"tian-user\"}");
            }
            if (Error) return Json("{\"code\":429,\"error_code\":429,\"message\":\"" + Secret + "\",\"error\":\"" + Secret + "\"}", HttpStatusCode.Forbidden);
            if (Redirect) return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example/" + Secret) }, Content = new StringContent("") };
            if (Kind == PanelKind.Tianyi)
            {
                Check(request.Headers.Contains("Signature") && request.Headers.Contains("SessionKey") && request.Headers.Date.HasValue, "Tianyi signed API request");
                Check(request.Headers.Authorization == null, "Tianyi excludes bearer token");
            }
            else if (Kind == PanelKind.Mobile139) Check(request.Headers.Authorization.Scheme == "Basic" && request.Headers.Contains("Mcloud-Sign"), "Mobile signed API request");
            else Check(request.Headers.Authorization?.Scheme == "Bearer", Kind + " API bearer authorization");
            switch (Kind)
            {
                case PanelKind.Aliyun:
                    if (path.EndsWith("getDriveInfo")) return Json("{\"user_id\":\"ali-user\",\"name\":\"Ali\",\"default_drive_id\":\"drive\"}");
                    if (path.EndsWith("getDownloadUrl")) return Json("{\"url\":\"https://cdn.example/original\"}");
                    Check(body.Contains("parent_file_id"), "Aliyun list parameters");
                    return Json("{\"items\":[{\"file_id\":\"f1\",\"name\":\"中文文件.bin\",\"type\":\"file\",\"size\":123},{\"file_id\":\"d1\",\"name\":\"目录\",\"type\":\"folder\"}],\"next_marker\":\"" + (Endless ? "same" : "") + "\"}");
                case PanelKind.Pan123:
                    Check(request.Headers.Contains("platform"), "123 platform header");
                    if (path.EndsWith("user/info")) return Json("{\"code\":0,\"data\":{\"uid\":123,\"nickname\":\"123\"}}");
                    if (path.EndsWith("download_info")) return Json("{\"code\":0,\"data\":{\"downloadUrl\":\"https://cdn.example/original\"}}");
                    if (MalformedList) return Json("{\"code\":0,\"data\":{\"fileList\":{},\"lastFileId\":-1}}");
                    return Json("{\"code\":0,\"data\":{\"fileList\":[{\"fileId\":1,\"filename\":\"中文文件.bin\",\"type\":0,\"size\":123},{\"fileId\":2,\"filename\":\"目录\",\"type\":1}],\"lastFileId\":" + (Endless ? "88" : "-1") + "}}");
                case PanelKind.Pan115:
                    if (path.EndsWith("user/info")) return Json("{\"state\":true,\"data\":{\"user_id\":115,\"user_name\":\"115\"}}");
                    if (path.EndsWith("downurl"))
                    {
                        Check(body == "pick_code=pick" && request.Content.Headers.ContentType.MediaType == "application/x-www-form-urlencoded", "115 pick code form");
                        return Json("{\"state\":true,\"data\":{\"1\":{\"url\":{\"url\":\"https://cdn.example/original\"}}}}");
                    }
                    return Json("{\"state\":true,\"count\":2,\"data\":[{\"fid\":\"1\",\"fn\":\"中文文件.bin\",\"fs\":123,\"fc\":\"1\",\"pc\":\"pick\"},{\"fid\":\"2\",\"fn\":\"目录\",\"fc\":\"0\"}]}");
                case PanelKind.Tianyi:
                    if (path.EndsWith("getUserInfo.action")) return Json("{\"userId\":\"tian-user\",\"nickName\":\"天翼\"}");
                    if (path.EndsWith("getFileDownloadUrl.action")) return Json("{\"fileDownloadUrl\":\"https://cdn.example/original\"}");
                    return Json("{\"fileListAO\":{\"count\":2,\"folderList\":[{\"id\":\"2\",\"name\":\"目录\"}],\"fileList\":[{\"id\":\"1\",\"name\":\"中文文件.bin\",\"size\":123}]}}");
                case PanelKind.Mobile139:
                    if (path.EndsWith("qryRoutePolicy")) return Json("{\"success\":true,\"data\":{\"routePolicyList\":[{\"modName\":\"personal\",\"httpsUrl\":\"" + (HostileRoute ? "https://attacker.example" : "https://personal.yun.139.com/api") + "\"}]}}");
                    if (path.EndsWith("getDownloadUrl")) return Json("{\"success\":true,\"data\":{\"cdnSwitch\":true,\"cdnUrl\":\"https://cdn.example/original\",\"url\":\"https://cdn.example/fallback\"}}");
                    return Json("{\"success\":true,\"data\":{\"items\":[{\"fileId\":\"1\",\"name\":\"中文文件.bin\",\"type\":\"file\",\"size\":123},{\"fileId\":\"2\",\"name\":\"目录\",\"type\":\"folder\"}],\"nextPageCursor\":\"\"}}");
                default:
                    Check(request.Headers.Contains("X-Device-Id") && request.Headers.Contains("X-Captcha-Token"), "Thunder session headers");
                    if (path.EndsWith("user/me")) return Json("{\"sub\":\"xun-user\",\"name\":\"迅雷\"}");
                    if (path.EndsWith("files/1")) return Json("{\"web_content_link\":\"https://cdn.example/original\"}");
                    return Json("{\"files\":[{\"id\":\"1\",\"name\":\"中文文件.bin\",\"kind\":\"drive#file\",\"size\":123},{\"id\":\"2\",\"name\":\"目录\",\"kind\":\"drive#folder\"}],\"next_page_token\":\"\"}");
            }
        }
        private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
    static NativeCloudAccount Credential() => new NativeCloudAccount { AccessToken = Secret, RefreshToken = "refresh", ClientId = "own-app", ClientSecret = "own-secret", SessionKey = "session", SessionSecret = "session-secret", DeviceId = "device", CaptchaToken = "captcha", Authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes("prefix:13900000000:token|expires")), UserAgent = "ZERA-Fixture/1" };
    static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (PanelApiException ex) { Check(!ex.Message.Contains(Secret) && !ex.ToString().Contains("attacker.example"), name); return; } throw new Exception("Expected failure: " + name); }
    static async Task Run()
    {
        Check(PanelRegistry.All().Count() == 10 && (int)PanelKind.WebDav == 2, "Registry preserves old IDs and exposes ten platforms");
        foreach (var kind in Enum.GetValues(typeof(PanelKind)).Cast<PanelKind>().Where(NativeCloudAccount.Supports))
        {
            var handler = new Fixture { Kind = kind };
            using (var client = new NativeCloudClient(kind, Credential().Serialize(), handler))
            {
                var login = await PanelAdapter.For(client).LoginAsync(); Check(login.Valid && !string.IsNullOrEmpty(login.Key), kind + " validated stable identity");
                var files = await client.ListDirAsync("0", 5); Check(files.Count == 2 && files.Single(f => !f.IsDir).Size == 123 && files.Any(f => f.IsDir), kind + " native directory and unicode metadata");
                string fid = files.Single(f => !f.IsDir).Fid;
                var link = await client.ResolveDriveFileAsync(fid); Check(link.DownloadUrl == "https://cdn.example/original" && link.AccountKey == login.Key, kind + " original file URL and account binding");
                Check(await client.RefreshLinkAsync(new ZERA.Core.Download.PendingInfo { Fid = fid }) == link.DownloadUrl, kind + " persisted ID resolves after renewal");
                var context = client.BuildDownloadContext(); Check(string.IsNullOrEmpty(context.Cookie) && string.IsNullOrEmpty(context.Authorization) && context.UserAgent == "ZERA-Fixture/1", kind + " CDN context contains no API credentials");
                Check(PanelAdapter.CredentialFingerprint(kind, client.CookieStr) == login.Key, kind + " identity fingerprint survives token rotation");
                handler.Error = true; await Reject(async () => { await client.ListDirAsync("0", 2); }, kind + " server failure redacts tokens"); handler.Error = false;
                handler.Redirect = true; await Reject(async () => { await client.ResolveDriveFileAsync(fid); }, kind + " API redirects refused"); handler.Redirect = false;
                if (kind == PanelKind.Aliyun || kind == PanelKind.Pan123 || kind == PanelKind.Pan115 || kind == PanelKind.Thunder)
                {
                    int changed = 0; client.CredentialsChanged += () => changed++;
                    handler.Unauthorized = true;
                    await client.ResolveDriveFileAsync(fid); Check(handler.RefreshCalls == 1 && changed == 1 && login.Key == client.AccountKey && client.CookieStr.Contains("renewed"), kind + " one bounded refresh with stable account and persistence event");
                }
                if (kind == PanelKind.Aliyun || kind == PanelKind.Pan123)
                { handler.Endless = true; await Reject(async () => { await client.ListDirAsync("0", 4); }, kind + " repeated pagination cursor rejected"); await Reject(async () => { await client.ListDirAsync("0", 1); }, kind + " incomplete pagination rejected"); }
            }
        }
        using (var client = new NativeCloudClient(PanelKind.Mobile139, Credential().Serialize(), new Fixture { Kind = PanelKind.Mobile139, HostileRoute = true }))
            await Reject(async () => { await client.ValidateAsync(); }, "Mobile refuses foreign route before credentials leave platform");
        foreach (var kind in new[] { PanelKind.Aliyun, PanelKind.Pan123, PanelKind.Pan115, PanelKind.Tianyi, PanelKind.Thunder })
        {
            var account = Credential(); account.AccessToken = kind == PanelKind.Tianyi ? Secret : "";
            account.SessionKey = ""; account.SessionSecret = "";
            using (var client = new NativeCloudClient(kind, account.Serialize(), new Fixture { Kind = kind }))
                Check((await client.ValidateAsync()).ok, kind + " initial authorization without existing access/session token");
        }
        var denied = new Fixture { Kind = PanelKind.Pan123, AlwaysUnauthorized = true };
        using (var client = new NativeCloudClient(PanelKind.Pan123, Credential().Serialize(), new Fixture { Kind = PanelKind.Pan123, MalformedList = true }))
            await Reject(async () => { await client.ListDirAsync("0", 3); }, "Malformed file list is never treated as empty directory");
        using (var client = new NativeCloudClient(PanelKind.Pan123, Credential().Serialize(), denied))
        { await Reject(async () => { await client.ValidateAsync(); }, "Invalid renewed token stops after one retry"); Check(denied.RefreshCalls == 1 && denied.Calls == 3, "Authorization failure has bounded request count"); }
        using (var client = new NativeCloudClient(PanelKind.Pan123, Credential().Serialize(), new Fixture { Kind = PanelKind.Pan123, Delay = true }))
        using (var cancel = new CancellationTokenSource(100))
        {
            try { await client.ValidateAsync(cancel.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "Native API cancellation propagates"); }
        }
        Console.WriteLine("ALL NATIVE CLOUD REGRESSIONS PASSED");
    }
}
