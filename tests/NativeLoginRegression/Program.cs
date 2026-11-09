using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using ZERA.Core.Panels;

static class Program
{
    const string Password = "TEST-PASSWORD-NEVER-PERSIST", Token = "TEST-ACCESS-TOKEN";
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static object At(object value, params string[] keys) { foreach (string key in keys) if (!(value is Dictionary<string, object> map) || !map.TryGetValue(key, out value)) return null; return value; }
    static string S(object value, params string[] keys) => Convert.ToString(At(value, keys)) ?? "";
    static object Json(string text) => new JavaScriptSerializer().DeserializeObject(text);
    static string Encode(object value) => new JavaScriptSerializer().Serialize(value);
    static Dictionary<string, object> Map(params object[] values) { var m = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) m.Add((string)values[i], values[i + 1]); return m; }
    sealed class Server : HttpMessageHandler
    {
        public PanelKind Kind; public int Polls, Calls, PasswordPosts, SmsPosts;
        public bool Captcha, Sms, Decline, ForeignRedirect, Delay, Fail, WrongPassword, MobileSsoFailure;
        public string Challenge, LastBody, LastUrl;
        public RSACryptoServiceProvider Rsa = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
        static byte[] Der(byte tag, byte[] value)
        { byte[] length = value.Length < 128 ? new[] { (byte)value.Length } : value.Length < 256 ? new byte[] { 0x81, (byte)value.Length } : new byte[] { 0x82, (byte)(value.Length >> 8), (byte)value.Length }; return new[] { tag }.Concat(length).Concat(value).ToArray(); }
        static byte[] Integer(byte[] data) => Der(2, data[0] >= 128 ? new byte[] { 0 }.Concat(data).ToArray() : data);
        public string PublicKey()
        {
            var key = Rsa.ExportParameters(false); byte[] pkcs = Der(0x30, Integer(key.Modulus).Concat(Integer(key.Exponent)).ToArray());
            byte[] algorithm = new byte[] { 0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00 };
            return Convert.ToBase64String(Der(0x30, algorithm.Concat(Der(3, new byte[] { 0 }.Concat(pkcs).ToArray())).ToArray()));
        }
        string Decrypt(string encrypted)
        { Check(encrypted.StartsWith("{NRP}"), "Tianyi server-provided RSA prefix"); string value = encrypted.Substring(5); byte[] bytes = Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray(); return Encoding.UTF8.GetString(Rsa.Decrypt(bytes, false)); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Calls++; if (Delay) await Task.Delay(30000, cancel);
            string path = request.RequestUri.AbsolutePath; string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(); LastBody = body; LastUrl = request.RequestUri.AbsoluteUri;
            Check(request.RequestUri.Scheme == "https" && request.Headers.Authorization == null, "Login uses HTTPS without previous account bearer token");
            if (Fail) return Reply(Encode(Map("message", Password, "error", Token)), HttpStatusCode.Forbidden);
            if (Kind == PanelKind.Aliyun)
            {
                if (path.EndsWith("authorize/qrcode")) { var data = Json(body); Check(S(data, "client_id") == "OWN-APP" && !body.Contains("file:all:write"), "Ali requests own app with read-only scopes"); return Reply(Encode(Map("sid", "qr-session", "qrCodeUrl", "https://openapi.alipan.com/qr-image"))); }
                if (path.EndsWith("qr-image")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")) };
                if (path.EndsWith("status")) { Polls++; return Reply(Encode(Map("status", Decline ? "QRCodeExpired" : Polls == 1 ? "WaitLogin" : Polls == 2 ? "ScanSuccess" : "LoginSuccess", "authCode", "one-time-code"))); }
                Check(S(Json(body), "code") == "one-time-code" && S(Json(body), "grant_type") == "authorization_code", "Ali exchanges auth code rather than QR session ID");
                return Reply(Encode(Map("access_token", Token, "refresh_token", "REFRESH")));
            }
            if (Kind == PanelKind.Pan115)
            {
                var form = HttpUtility.ParseQueryString(body);
                if (path.EndsWith("authDeviceCode")) { Check(form["client_id"] == "OWN-APP" && form["code_challenge_method"] == "sha256", "115 own app PKCE request"); Challenge = form["code_challenge"]; return Reply(Encode(Map("state", true, "data", Map("uid", "uid", "time", 123, "sign", "signed", "qrcode", "https://115.com/scan")))); }
                if (path.Contains("/get/status")) { Polls++; return Reply(Encode(Map("state", true, "data", Map("status", Decline ? -2 : Polls == 1 ? 0 : Polls == 2 ? 1 : 2)))); }
                using (var sha = SHA256.Create()) Check(Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(form["code_verifier"]))) == Challenge, "115 PKCE verifier matches SHA256 challenge");
                return Reply(Encode(Map("state", true, "data", Map("access_token", Token, "refresh_token", "REFRESH"))));
            }
            if (Kind == PanelKind.Tianyi)
            {
                if (path.EndsWith("unifyLoginForPC.action")) return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(ForeignRedirect ? "https://attacker.example/?lt=lt&reqId=req" : "https://open.e.189.cn/index.html?lt=lt&reqId=req") }, Content = new StringContent("") };
                if (path == "/index.html") return Reply("<input name='captchaToken' value='captcha-ticket' />");
                if (path.EndsWith("appConf.do")) return Reply(Encode(Map("result", 0, "data", Map("paramId", "PARAM"))));
                if (path.EndsWith("encryptConf.do")) return Reply(Encode(Map("result", 0, "data", Map("pubKey", PublicKey(), "pre", "{NRP}"))));
                if (path.EndsWith("getUUID.do")) return Reply(Encode(Map("uuid", "TIANYI-QR-TEXT", "encryuuid", "enc")));
                if (path.EndsWith("qrcodeLoginState.do")) { Polls++; var form = HttpUtility.ParseQueryString(body); Check(form["clientType"] == "1" && form["paramId"] == "PARAM", "Tianyi QR uses correct client type and transaction"); return Reply(Encode(Map("status", Decline ? -11001 : Polls == 1 ? -106 : Polls == 2 ? -11002 : 0, "redirectUrl", "https://cloud.189.cn/redirect?ticket=one-time"))); }
                if (path.EndsWith("needcaptcha.do")) { Check(Decrypt(HttpUtility.ParseQueryString(body)["userName"]) == "test-user", "Tianyi username RSA encrypted"); return Reply(Captcha ? "1" : "0"); }
                if (path.EndsWith("picCaptcha.do")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")) };
                if (path.EndsWith("loginSubmit.do"))
                {
                    PasswordPosts++; var form = HttpUtility.ParseQueryString(body); Check(!body.Contains(Password) && Decrypt(form["password"]) == Password && form["password"] == form["epd"], "Tianyi password encrypted before submission");
                    if (Captcha) Check(form["validateCode"] == "ABCD", "Tianyi picture answer sent in same transaction");
                    return Reply(Encode(Map("result", WrongPassword ? -1 : Sms ? -133 : 0, "mobile", "MASKED-MOBILE", "toUrl", WrongPassword || Sms ? "" : "https://cloud.189.cn/redirect?ticket=password")));
                }
                if (path.EndsWith("sendSmsCodeForSecondAuth.do")) { SmsPosts++; return Reply(Encode(Map("result", 0))); }
                if (path.EndsWith("submitForSecondAuth.do")) { Check(Decrypt(HttpUtility.ParseQueryString(body)["epd"]) == "123456", "Tianyi SMS answer RSA encrypted"); return Reply(Encode(Map("result", 0, "toUrl", "https://cloud.189.cn/redirect?ticket=sms"))); }
                Check(path.EndsWith("getSessionForPC.action") && HttpUtility.ParseQueryString(request.RequestUri.Query)["redirectURL"].Contains("ticket="), "Tianyi login ticket exchanges PC session");
                var result = Reply(Encode(Map("res_code", 0, "sessionKey", "SESSION", "sessionSecret", "SESSION-SECRET", "accessToken", Token, "refreshToken", "REFRESH"))); return result;
            }
            if (Kind == PanelKind.Pan123)
            {
                PasswordPosts++; var data = Json(body); Check(path.EndsWith("user/sign_in") && S(data, "password") == Password && string.Join(" ", request.Headers.GetValues("User-Agent")).StartsWith("Dart/"), "123 password sent to dedicated login endpoint with matching login client headers");
                return Reply(Encode(Map("code", WrongPassword ? 401 : 200, "message", Password, "data", Map("token", WrongPassword ? "" : Token))));
            }
            if (Kind == PanelKind.Thunder)
            {
                if (path.Contains("core.login")) { PasswordPosts++; Check(S(Json(body), "passWord") == Password && S(Json(body), "devicesign").StartsWith("div101."), "Thunder core password login and device signature"); return Reply(Encode(Map("sessionID", WrongPassword ? "" : "CORE-SESSION", "error", WrongPassword ? Password : ""))); }
                if (path.EndsWith("captcha/init")) { Check(string.Join(" ", request.Headers.GetValues("User-Agent")).StartsWith("ANDROID-"), "Thunder token shield uses application user agent"); return Reply(Encode(Map("captcha_token", "CAPTCHA"))); }
                Check(S(Json(body), "signin_token") == "CORE-SESSION" && request.Headers.Contains("X-Captcha-Token"), "Thunder exchanges core session with captcha context");
                return Reply(Encode(Map("access_token", Token, "refresh_token", "REFRESH")));
            }
            // Mobile password + mail SSO + encrypted cloud token exchange.
            if (path.EndsWith("Login.ashx"))
            {
                PasswordPosts++; Check(!body.Contains(Password) && HttpUtility.ParseQueryString(body)["Password"].Length == 40, "Mobile sends password hash rather than plaintext");
                if (WrongPassword) return Reply("password rejected");
                var response = Reply(""); response.Headers.Add("Set-Cookie", "RMKEY=MAIL-COOKIE; Domain=mail.10086.cn; Path=/");
                if (Sms) { response.StatusCode = HttpStatusCode.Redirect; response.Headers.Location = new Uri("https://mail.10086.cn/?ec=MW0016"); }
                else response.Headers.Add("Set-Cookie", "Os_SSo_Sid=SID; Domain=mail.10086.cn; Path=/");
                return response;
            }
            if (path == "/s")
            {
                Check(!body.Contains("test-user") && !body.Contains("123456"), "Mobile SMS username is RSA encrypted and code is hashed");
                if (request.RequestUri.Query.Contains("sendSmsCodeByScene")) { SmsPosts++; return Reply(Encode(Map("code", "S_OK"))); }
                var reply = Reply(Encode(Map("code", "S_OK", "var", Map("loginSuccessUrl", "https://mail.10086.cn/?sid=SMS-SID")))); return reply;
            }
            if (path.EndsWith("/setting/s")) { Check(request.Headers.GetValues("Cookie").Single().Contains("RMKEY="), "Mobile mail cookie sent only to mail artifact endpoint"); return Reply(Encode(Map("var", Map("artifact", "ONE-TIME-ARTIFACT")))); }
            Check(path.EndsWith("thirdlogin") && !body.Contains("ONE-TIME-ARTIFACT") && !body.Contains(Password), "Mobile cloud SSO request encrypted");
            if (MobileSsoFailure) return Reply("SSO rejected", HttpStatusCode.Forbidden);
            byte[] inner = Crypt(Encoding.UTF8.GetBytes(Encode(Map("account", "13900000000", "authToken", "CLOUD-AUTH"))), Hex("7150714477323633586746674c337538"), null, true);
            return Reply(Encode(Map("data", BitConverter.ToString(inner).Replace("-", ""))));
        }
        public static byte[] Hex(string text) => Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
        public static byte[] Crypt(byte[] bytes, byte[] key, byte[] iv, bool encrypt) { using (var aes = Aes.Create()) { aes.Key = key; aes.Mode = iv == null ? CipherMode.ECB : CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; if (iv != null) aes.IV = iv; using (var crypto = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor()) return crypto.TransformFinalBlock(bytes, 0, bytes.Length); } }
        static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
    sealed class Web123 : HttpMessageHandler
    {
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Check(request.RequestUri.Host == "yun.123pan.com" && request.RequestUri.Query.Contains("=") && request.Headers.Authorization.Parameter == Token && request.Headers.GetValues("platform").Single() == "web", "123 website token scoped to matching website API");
            object data;
            if (request.RequestUri.AbsolutePath.EndsWith("user/info")) data = Map("UID", 123, "Nickname", "Web account");
            else if (request.RequestUri.AbsolutePath.EndsWith("list/new")) data = Map("InfoList", new[] { Map("FileId", 7, "FileName", "文件.bin", "Type", 0, "Size", 16, "Etag", "ETAG", "S3KeyFlag", "S3") }, "Next", "-1", "Total", 1);
            else { var body = Json(await request.Content.ReadAsStringAsync()); Check(S(body, "etag") == "ETAG" && S(body, "s3keyFlag") == "S3", "123 resumed link request retains file metadata"); data = Map("DownloadUrl", "https://cdn.example/file?signature=test"); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Encode(Map("code", Fail ? 401 : 0, "message", Password, "data", data))) };
        }
    }
    sealed class Gateway : HttpMessageHandler
    {
        public bool JsonResponse;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Check(request.RequestUri.Host == "cdn.example" && request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "123 gateway does not receive API credentials");
            Check(request.Headers.Referrer.AbsoluteUri == "https://cdn.example/", "123 gateway uses its own file referer");
            return Task.FromResult(JsonResponse ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"redirect_url\":\"https://download.example/original\"}}", Encoding.UTF8, "application/json") } :
                new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://download.example/original") }, Content = new StringContent("") });
        }
    }
    static NativeCloudAccount Options() => new NativeCloudAccount { ClientId = "OWN-APP", ClientSecret = "OWN-SECRET", AccessToken = "OLD-ACCOUNT", Identity = "OLD-ID" };
    static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (Exception ex) when (ex is PanelApiException || ex is InvalidOperationException) { Check(!ex.ToString().Contains(Password) && !ex.ToString().Contains(Token) && !ex.ToString().Contains("attacker.example"), name); return; } throw new Exception("Expected rejection: " + name); }
    static async Task Run()
    {
        foreach (var kind in new[] { PanelKind.Aliyun, PanelKind.Pan115, PanelKind.Tianyi })
        {
            var server = new Server { Kind = kind };
            using (var session = new NativeLoginSession(kind, Options(), server))
            {
                var qr = await session.StartQrAsync(); Check(!string.IsNullOrEmpty(qr.QrText) || qr.Image?.Length > 0, kind + " returns QR payload or official QR image");
                Check((await session.PollQrAsync()).State == NativeLoginState.Waiting && (await session.PollQrAsync()).State == NativeLoginState.Scanned, kind + " waiting and phone confirmation states");
                var done = await session.PollQrAsync(); Check(done.State == NativeLoginState.Success && done.Account.Identity == "" && !done.Account.Serialize().Contains("OLD-ACCOUNT"), kind + " success returns fresh credentials, not old identity");
                await Reject(async () => { await session.PollQrAsync(); }, kind + " authorization result consumed once");
            }
            using (var session = new NativeLoginSession(kind, Options(), new Server { Kind = kind, Decline = true }))
            { await session.StartQrAsync(); var result = await session.PollQrAsync(); Check(result.State == NativeLoginState.Expired || result.State == NativeLoginState.Cancelled, kind + " expiry or decline terminates polling"); }
        }
        foreach (var kind in new[] { PanelKind.Tianyi, PanelKind.Pan123, PanelKind.Mobile139, PanelKind.Thunder })
        {
            using (var session = new NativeLoginSession(kind, Options(), new Server { Kind = kind }))
            {
                var result = await session.PasswordAsync("test-user", Password);
                Check(result.State == NativeLoginState.Success && !result.Account.Serialize().Contains(Password), kind + " password login returns reusable session without saving password");
                if (kind == PanelKind.Thunder)
                {
                    using (var client = new NativeCloudClient(kind, result.Account.Serialize()))
                        Check(result.Account.LoginMode == "android" && result.Account.UserAgent.StartsWith("ANDROID-") && client.BuildDownloadContext().UserAgent.StartsWith("Dalvik/"), "Thunder API and file user agents match their corresponding SDK roles");
                }
                if (kind == PanelKind.Pan123)
                {
                    Check(result.Account.LoginMode == "web", "123 login mode persisted separately from open API");
                    var webApi = new Web123();
                    using (var web = new Pan123WebClient(result.Account.Serialize(), webApi, new Gateway()))
                    {
                        var login = await PanelAdapter.For(web).LoginAsync(); var files = await web.ListDirAsync("0", 5); string id = files.Single().Fid;
                        Check(login.Valid && login.Key.StartsWith("Pan123Web_") && PanelAdapter.CredentialFingerprint(PanelKind.Pan123, web.CookieStr) == login.Key, "123 web account identity and encrypted profile fingerprint agree");
                        Check((await web.ResolveDriveFileAsync(id)).DownloadUrl == "https://download.example/original" && !web.BuildDownloadContext(id).Authorization.Contains(Token) && web.BuildDownloadContext(id).Referer == "https://cdn.example/", "123 password login reaches directory and real file gateway");
                        var saved = web.CookieStr;
                        using (var restarted = new Pan123WebClient(saved, new Web123(), new Gateway { JsonResponse = true })) Check((await restarted.ResolveDriveFileAsync(id)).DownloadUrl == "https://download.example/original", "123 website task refreshes after restart and resolves JSON gateway");
                        webApi.Fail = true; await Reject(async () => { await web.ValidateAsync(); }, "123 website expiry reports re-login without echoing server secrets");
                    }
                    using (var registry = PanelRegistry.Create(PanelKind.Pan123, result.Account.Serialize())) Check(registry is Pan123WebClient, "123 factory selects matching website client");
                }
            }
        }
        foreach (var kind in new[] { PanelKind.Tianyi, PanelKind.Mobile139 })
        {
            var fixture = new Server { Kind = kind, Sms = true, Captcha = kind == PanelKind.Tianyi };
            using (var session = new NativeLoginSession(kind, Options(), fixture))
            {
                var state = await session.PasswordAsync("test-user", Password);
                if (kind == PanelKind.Tianyi) { Check(state.State == NativeLoginState.Captcha && state.Image.Length > 0 && fixture.PasswordPosts == 0, "Tianyi waits for picture answer before password submission"); state = await session.SubmitChallengeAsync("ABCD"); }
                Check(state.State == NativeLoginState.SmsRequired && fixture.SmsPosts == 0, kind + " SMS waits for explicit send button");
                await Reject(async () => { await session.PasswordAsync("other-user", Password); }, kind + " pending verification cannot switch account mid-transaction");
                Check((await session.SubmitChallengeAsync("send-sms")).State == NativeLoginState.SmsSent && fixture.SmsPosts == 1, kind + " SMS sent exactly once");
                await Reject(async () => { await session.SubmitChallengeAsync("send-sms"); }, kind + " duplicate SMS send rejected");
                Check((await session.SubmitChallengeAsync("123456")).State == NativeLoginState.Success, kind + " SMS completes original login transaction");
            }
        }
        foreach (var kind in new[] { PanelKind.Tianyi, PanelKind.Pan123, PanelKind.Thunder })
            using (var session = new NativeLoginSession(kind, Options(), new Server { Kind = kind, WrongPassword = true })) await Reject(async () => { await session.PasswordAsync("test-user", Password); }, kind + " bad password rejected without secret leakage");
        var staleMail = new Server { Kind = PanelKind.Mobile139, MobileSsoFailure = true };
        using (var session = new NativeLoginSession(PanelKind.Mobile139, Options(), staleMail))
        {
            await Reject(async () => { await session.PasswordAsync("test-user", Password); }, "Mobile failed cloud SSO does not count as login");
            staleMail.WrongPassword = true; staleMail.MobileSsoFailure = false; int before = staleMail.Calls;
            await Reject(async () => { await session.PasswordAsync("other-user", Password); }, "Mobile failed new password cannot reuse old mail identity");
            Check(staleMail.Calls == before + 1, "Mobile old mail cookie is cleared before a new password attempt");
        }
        var escaped = new Server { Kind = PanelKind.Tianyi, ForeignRedirect = true };
        using (var session = new NativeLoginSession(PanelKind.Tianyi, Options(), escaped))
        { await Reject(async () => { await session.StartQrAsync(); }, "Foreign login redirect rejected before following"); Check(escaped.Calls == 1, "Foreign endpoint never contacted"); }
        using (var session = new NativeLoginSession(PanelKind.Aliyun, new NativeCloudAccount(), new Server { Kind = PanelKind.Aliyun })) await Reject(async () => { await session.StartQrAsync(); }, "Missing app credentials fail locally");
        using (var session = new NativeLoginSession(PanelKind.Pan123, Options(), new Server { Kind = PanelKind.Pan123, Fail = true })) await Reject(async () => { await session.PasswordAsync("test-user", Password); }, "HTTP failures do not print raw server body");
        using (var session = new NativeLoginSession(PanelKind.Pan115, Options(), new Server { Kind = PanelKind.Pan115, Delay = true }))
        using (var cancel = new CancellationTokenSource(50))
        { try { await session.StartQrAsync(cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "Closing login cancels outstanding request"); } }
        Check(Pan123WebClient.Crc32("123456789") == 0xcbf43926, "123 web CRC32 signature matches standard vector");
        Console.WriteLine("ALL NATIVE LOGIN REGRESSIONS PASSED");
    }
    static int Main() { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; } }
}
