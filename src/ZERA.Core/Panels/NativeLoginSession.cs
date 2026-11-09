using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace ZERA.Core.Panels
{
    public enum NativeLoginState { Waiting, Scanned, Captcha, SmsRequired, SmsSent, Verification, Success, Expired, Cancelled }
    public sealed class NativeLoginResult
    {
        public NativeLoginState State { get; set; }
        public string QrText { get; set; }
        public byte[] Image { get; set; }
        public string Hint { get; set; }
        public NativeCloudAccount Account { get; set; }
        public string VerificationUrl { get; set; }
    }

    // A short-lived login transaction. Passwords and verification codes are never serialized.
    public sealed partial class NativeLoginSession : IDisposable
    {
        private readonly HttpClient _http;
        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly NativeCloudAccount _account;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private readonly DateTime _expires = DateTime.UtcNow.AddMinutes(5);
        private readonly PanelKind _kind;
        private string _sid, _sign, _time, _verifier;
        private bool _consumed;
        public static bool SupportsQr(PanelKind kind) => kind == PanelKind.Aliyun || kind == PanelKind.Pan115 || kind == PanelKind.Tianyi;
        public static bool SupportsPassword(PanelKind kind) => kind == PanelKind.Pan123 || kind == PanelKind.Tianyi || kind == PanelKind.Mobile139 || kind == PanelKind.Thunder;
        public NativeLoginSession(PanelKind kind, NativeCloudAccount options, HttpMessageHandler handler = null)
        {
            if (!NativeCloudAccount.Supports(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            _kind = kind; _account = NativeCloudAccount.Parse((options ?? new NativeCloudAccount()).Serialize());
            // Never reuse an old account's tokens/identity when starting a different login.
            _account.AccessToken = _account.RefreshToken = _account.Authorization = _account.Identity = _account.DriveId = _account.SessionKey = _account.SessionSecret = "";
            _account.LoginMode = "";
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = _cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
        }
        private async Task<NativeLoginResult> Transaction(Func<CancellationToken, Task<NativeLoginResult>> action, CancellationToken cancel)
        {
            using (var scope = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closed.Token))
            {
                await _gate.WaitAsync(scope.Token).ConfigureAwait(false);
                try
                {
                    if (_consumed) throw new InvalidOperationException("本次登录已结束，请点击重新开始。");
                    var remaining = _expires - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return new NativeLoginResult { State = NativeLoginState.Expired, Hint = "登录会话已过期，请重新开始。" };
                    scope.CancelAfter(remaining);
                    var result = await action(scope.Token).ConfigureAwait(false);
                    if (result.State == NativeLoginState.Success || result.State == NativeLoginState.Expired || result.State == NativeLoginState.Cancelled) { _consumed = true; _encryptedPassword = null; }
                    return result;
                }
                finally { _gate.Release(); }
            }
        }
        private static Dictionary<string, object> Dict(object value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
        private static object At(object value, params string[] keys) { foreach (string key in keys) if (!Dict(value).TryGetValue(key, out value)) return null; return value; }
        private static string Text(object value, params string[] keys) => Convert.ToString(At(value, keys), CultureInfo.InvariantCulture) ?? "";
        private static Dictionary<string, object> Map(params object[] pairs) { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]); return result; }
        private static string Query(params object[] pairs) => string.Join("&", Map(pairs).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Convert.ToString(p.Value, CultureInfo.InvariantCulture))));
        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
        private static string Sha1(string text) { using (var hash = SHA1.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        private static string Md5(string text) { using (var hash = MD5.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        private static byte[] Random(int count) { var bytes = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        private static bool SafeUrl(string url, params string[] hosts) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && hosts.Contains(uri.Host);
        private bool Allowed(string url)
        {
            switch (_kind)
            {
                case PanelKind.Aliyun: return SafeUrl(url, "openapi.alipan.com", "openapi.aliyundrive.com");
                case PanelKind.Pan115: return SafeUrl(url, "passportapi.115.com", "qrcodeapi.115.com");
                case PanelKind.Tianyi: return SafeUrl(url, "cloud.189.cn", "open.e.189.cn", "api.cloud.189.cn");
                case PanelKind.Pan123: return SafeUrl(url, "login.123pan.com");
                case PanelKind.Mobile139: return SafeUrl(url, "mail.10086.cn", "smsrebuild1.mail.10086.cn", "user-njs.yun.139.com");
                case PanelKind.Thunder: return SafeUrl(url, "xluser-ssl.xunlei.com");
                default: return false;
            }
        }
        private sealed class Reply { public byte[] Bytes; public Uri Location; public int Status; public string Body => Encoding.UTF8.GetString(Bytes); public object Json { get { try { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(Body); } catch { throw new PanelApiException("登录服务返回格式无效，请重新开始或稍后重试。"); } } } }
        private async Task<Reply> Request(string url, object data, CancellationToken cancel, bool form = false, Dictionary<string, string> headers = null, bool allowRedirect = false)
        {
            if (!Allowed(url)) throw new PanelApiException("登录地址超出所选平台范围，已停止发送凭据。");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (var request = new HttpRequestMessage(data == null ? HttpMethod.Get : HttpMethod.Post, url))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                request.Headers.TryAddWithoutValidation("User-Agent", _account.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (headers != null) foreach (var pair in headers) { request.Headers.Remove(pair.Key); request.Headers.TryAddWithoutValidation(pair.Key, pair.Value); }
                if (data is string xml) request.Content = new StringContent(xml, Encoding.UTF8, _kind == PanelKind.Mobile139 && url.Contains("thirdlogin") ? "text/plain" : "application/xml");
                else if (data != null) request.Content = form ? (HttpContent)new FormUrlEncodedContent(Dict(data).ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture))) : new StringContent(new JavaScriptSerializer().Serialize(data), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    using (deadline.Token.Register(() => response.Dispose()))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var memory = new MemoryStream())
                    {
                        int status = (int)response.StatusCode;
                        // Cookies retain the provider's Domain/Path scoping even when a fake handler is used in tests.
                        if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (string value in values) try { _cookies.SetCookies(request.RequestUri, value); } catch (CookieException) { }
                        if (status >= 300 && status < 400 && !allowRedirect) throw new PanelApiException("登录接口意外重定向，已停止发送凭据。", status: status);
                        if (status < 200 || status >= 400) throw new PanelApiException("登录服务请求失败（HTTP " + status + "）。请检查网络、账号权限或稍后重试。", status: status);
                        var buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                        { if (memory.Length + count > 4 * 1024 * 1024) throw new PanelApiException("登录响应过大，已停止读取。"); memory.Write(buffer, 0, count); }
                        Uri location = response.Headers.Location == null ? null : new Uri(request.RequestUri, response.Headers.Location);
                        return new Reply { Bytes = memory.ToArray(), Location = location, Status = status };
                    }
                }
                catch (HttpRequestException) { throw new PanelApiException("登录服务连接失败，请检查网络或代理后重试。"); }
            }
        }
        private static void Require(bool valid, string message) { if (!valid) throw new PanelApiException(message); }
        private NativeLoginResult Success()
        { _account.Validate(_kind); return new NativeLoginResult { State = NativeLoginState.Success, Account = _account, Hint = "授权完成，正在验证网盘账号…" }; }
        private static NativeLoginResult Waiting(bool scanned = false) => new NativeLoginResult { State = scanned ? NativeLoginState.Scanned : NativeLoginState.Waiting, Hint = scanned ? "已扫码，请在手机上确认。" : "等待使用本平台 App 扫码。" };
        public Task<NativeLoginResult> StartQrAsync(CancellationToken cancel = default) => Transaction(async ct =>
        {
            if (!SupportsQr(_kind)) throw new NotSupportedException("此平台暂未接入扫码登录。");
            Require(_sid == null, "二维码已生成，请关闭窗口后重新获取。");
            if (_kind == PanelKind.Tianyi) return await TianyiQr(ct).ConfigureAwait(false);
            if (_kind == PanelKind.Aliyun)
            {
                Require(_account.ClientId.Length > 0 && _account.ClientSecret.Length > 0, "阿里云盘扫码需要已获授权的应用 Client ID 与 Client Secret。");
                object result = (await Request("https://openapi.alipan.com/oauth/authorize/qrcode", Map("client_id", _account.ClientId, "client_secret", _account.ClientSecret, "scopes", new[] { "user:base", "file:all:read" }), ct).ConfigureAwait(false)).Json;
                _sid = Text(result, "sid"); string qr = Text(result, "qrCodeUrl");
                Require(_sid.Length > 0 && SafeUrl(qr, "openapi.alipan.com", "openapi.aliyundrive.com"), "阿里云盘未返回有效授权二维码图片地址，请核对应用权限。");
                // qrCodeUrl is an image resource, not the QR payload. Encoding its URL would create a different code.
                var image = await Request(qr, null, ct, headers: new Dictionary<string, string> { { "Accept", "image/png,image/jpeg" } }).ConfigureAwait(false);
                Require(image.Bytes.Length > 8 && (image.Bytes[0] == 137 && image.Bytes[1] == 80 || image.Bytes[0] == 255 && image.Bytes[1] == 216), "阿里云盘二维码图片响应无效，请重新获取。");
                return new NativeLoginResult { State = NativeLoginState.Waiting, Image = image.Bytes, Hint = "请使用阿里云盘 App 扫码并授权读取文件。" };
            }
            Require(_account.ClientId.Length > 0, "115 扫码需要开放平台应用 App ID（Client ID）。");
            _verifier = Convert.ToBase64String(Random(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); string challenge;
            using (var sha = SHA256.Create()) challenge = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(_verifier)));
            var response = (await Request("https://passportapi.115.com/open/authDeviceCode", Map("client_id", _account.ClientId, "code_challenge", challenge, "code_challenge_method", "sha256"), ct, true).ConfigureAwait(false)).Json;
            Require(Text(response, "state").Equals("True", StringComparison.OrdinalIgnoreCase), "115 未能建立扫码授权，请核对 App ID 与应用授权范围。");
            _sid = Text(response, "data", "uid"); _time = Text(response, "data", "time"); _sign = Text(response, "data", "sign");
            string code = Text(response, "data", "qrcode"); Require(_sid.Length > 0 && _time.Length > 0 && _sign.Length > 0 && code.Length > 0, "115 返回的二维码信息不完整。");
            return new NativeLoginResult { State = NativeLoginState.Waiting, QrText = code, Hint = "请使用 115 App 扫码并确认开放平台授权。" };
        }, cancel);
        public Task<NativeLoginResult> PollQrAsync(CancellationToken cancel = default) => Transaction(async ct =>
        {
            Require(!string.IsNullOrEmpty(_sid), "请先获取二维码。");
            if (_kind == PanelKind.Tianyi) return await TianyiPoll(ct).ConfigureAwait(false);
            object token;
            if (_kind == PanelKind.Aliyun)
            {
                var status = (await Request("https://openapi.alipan.com/oauth/qrcode/" + Uri.EscapeDataString(_sid) + "/status", null, ct).ConfigureAwait(false)).Json;
                string state = Text(status, "status");
                if (state == "WaitLogin") return Waiting();
                if (state == "ScanSuccess") return Waiting(true);
                if (state == "QRCodeExpired") return new NativeLoginResult { State = NativeLoginState.Expired, Hint = "二维码已过期，请重新获取。" };
                if (state == "LoginCanceled" || state == "LoginCancelled") return new NativeLoginResult { State = NativeLoginState.Cancelled, Hint = "手机已取消授权。" };
                Require(state == "LoginSuccess" && Text(status, "authCode").Length > 0, "阿里云盘返回未知扫码状态，请重新获取二维码。");
                token = (await Request("https://openapi.alipan.com/oauth/access_token", Map("client_id", _account.ClientId, "client_secret", _account.ClientSecret, "grant_type", "authorization_code", "code", Text(status, "authCode")), ct).ConfigureAwait(false)).Json;
            }
            else
            {
                var status = (await Request("https://qrcodeapi.115.com/get/status/?" + Query("uid", _sid, "time", _time, "sign", _sign), null, ct).ConfigureAwait(false)).Json;
                Require(At(status, "data", "status") != null, "115 扫码状态响应无效。"); string state = Text(status, "data", "status");
                if (state == "0" || state == "1") return Waiting(state == "1");
                if (state == "-1" || state == "-2") return new NativeLoginResult { State = state == "-1" ? NativeLoginState.Expired : NativeLoginState.Cancelled, Hint = "二维码已过期或授权已取消，请重新获取。" };
                Require(state == "2", "115 返回未知扫码状态，请重新获取二维码。");
                var response = (await Request("https://passportapi.115.com/open/deviceCodeToToken", Map("uid", _sid, "code_verifier", _verifier), ct, true).ConfigureAwait(false)).Json;
                Require(Text(response, "state").Equals("True", StringComparison.OrdinalIgnoreCase), "115 授权码兑换失败，请重新扫码。"); token = At(response, "data");
            }
            _account.AccessToken = Text(token, "access_token"); _account.RefreshToken = Text(token, "refresh_token");
            Require(_account.AccessToken.Length > 0 && _account.RefreshToken.Length > 0, "授权服务未返回完整令牌，请重新扫码。"); return Success();
        }, cancel);
        public Task<NativeLoginResult> PasswordAsync(string username, string password, CancellationToken cancel = default) => Transaction(async ct =>
        {
            if (!SupportsPassword(_kind)) throw new NotSupportedException("此平台未提供已接入的账号密码授权接口，请使用扫码授权。");
            Require(!_challenge.HasValue, "请先完成当前验证，或重新开始登录后再更换账号。");
            Require(!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password), "请填写账号和密码。");
            _username = username.Trim();
            switch (_kind)
            {
                case PanelKind.Tianyi: return await TianyiPassword(password, ct).ConfigureAwait(false);
                case PanelKind.Pan123:
                    object data = _username.Contains("@") ? Map("mail", _username, "password", password, "type", 2) : Map("passport", _username, "password", password, "remember", true);
                    var response = (await Request("https://login.123pan.com/api/user/sign_in", data, ct, headers: new Dictionary<string, string> { { "Origin", "https://yun.123pan.com" }, { "Referer", "https://yun.123pan.com/" }, { "platform", "web" }, { "app-version", "3" }, { "User-Agent", "Dart/2.19(dart:io)-ZERA" } }).ConfigureAwait(false)).Json;
                    Require(Text(response, "code") == "200" && Text(response, "data", "token").Length > 0, "123 登录失败，请核对账号密码；如平台要求验证，请先在官方页面完成后重试。");
                    _account.AccessToken = Text(response, "data", "token"); _account.LoginMode = "web"; _account.ClientId = _account.ClientSecret = ""; return Success();
                case PanelKind.Mobile139: return await MobilePassword(password, ct).ConfigureAwait(false);
                default: return await ThunderPassword(password, ct).ConfigureAwait(false);
            }
        }, cancel);
        public Task<NativeLoginResult> SubmitChallengeAsync(string code, CancellationToken cancel = default) => Transaction(ct =>
        {
            Require(!string.IsNullOrWhiteSpace(code), "请填写验证码，或先点击发送短信。");
            if (_kind == PanelKind.Tianyi) return TianyiChallenge(code.Trim(), ct);
            if (_kind == PanelKind.Mobile139) return MobileChallenge(code.Trim(), ct);
            throw new NotSupportedException("当前平台没有待提交的验证码。");
        }, cancel);
        public void Dispose() { _closed.Cancel(); _http.Dispose(); _encryptedPassword = _verifier = null; }
    }
}
