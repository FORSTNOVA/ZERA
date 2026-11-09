using System;
using System.Collections;
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
using System.Web.Script.Serialization;
using ZERA.Core.Models;

namespace ZERA.Core.Panels
{
    // Native protocols: API credentials are attached per request, never to download URLs.
    public sealed class NativeCloudClient : IPanelClient
    {
        private const string Ali = "https://openapi.alipan.com";
        private const string P123 = "https://open-api.123pan.com";
        private const string P115 = "https://proapi.115.com";
        private const string Tian = "https://api.cloud.189.cn";
        private const string Xun = "https://api-pan.xunlei.com/drive/v1";
        private const string Xauth = "https://xluser-ssl.xunlei.com/v1";
        private readonly NativeCloudAccount _account;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private string _mobileHost;
        private bool _configured;
        public PanelKind Kind { get; }
        public bool IsLoggedIn => _configured;
        public string CookieStr => _configured ? _account.Serialize() : "";
        public string AccountKey => string.IsNullOrEmpty(_account.Identity) ? null : PanelAdapter.CredentialKey(Kind + "_", _account.Identity);
        public event Action CredentialsChanged;
        public NativeCloudClient(PanelKind kind, string credential, HttpMessageHandler handler = null)
        {
            if (!NativeCloudAccount.Supports(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Kind = kind; _configured = !string.IsNullOrWhiteSpace(credential);
            _account = _configured ? NativeCloudAccount.Parse(credential) : new NativeCloudAccount();
            if (_configured) _account.Validate(kind);
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
        }
        private async Task<T> Locked<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancel)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try { if (!_configured) throw new PanelApiException("请先配置网盘授权。"); return await action(cancel).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        private static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object> ?? new Dictionary<string, object>();
        private static object At(object value, params string[] path)
        { foreach (var key in path) { var map = Obj(value); if (!map.TryGetValue(key, out value)) return null; } return value; }
        private static string Str(object value, params string[] path) => Convert.ToString(At(value, path), CultureInfo.InvariantCulture) ?? "";
        private static long Num(object value, params string[] path) => long.TryParse(Str(value, path), out var n) ? n : 0;
        private static IEnumerable<object> Arr(object value) => value is IEnumerable list && !(value is string) && !(value is IDictionary) ? list.Cast<object>() : Enumerable.Empty<object>();
        private static Dictionary<string, object> Map(params object[] pairs)
        { var map = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) map.Add((string)pairs[i], pairs[i + 1]); return map; }
        private static string Query(params object[] pairs) => string.Join("&", Map(pairs).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Convert.ToString(p.Value, CultureInfo.InvariantCulture))));
        private static bool MobileOrigin(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
            (uri.Host.EndsWith(".yun.139.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "yun.139.com");
        private bool Allowed(Uri uri)
        {
            if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
            switch (Kind)
            {
                case PanelKind.Aliyun: return uri.Host == "openapi.alipan.com";
                case PanelKind.Pan123: return uri.Host == "open-api.123pan.com";
                case PanelKind.Pan115: return uri.Host == "proapi.115.com" || uri.Host == "passportapi.115.com";
                case PanelKind.Tianyi: return uri.Host == "api.cloud.189.cn";
                case PanelKind.Mobile139: return MobileOrigin(uri);
                case PanelKind.Thunder: return uri.Host == "api-pan.xunlei.com" || uri.Host == "xluser-ssl.xunlei.com";
                default: return false;
            }
        }
        private async Task<Dictionary<string, object>> Send(string url, object body, CancellationToken cancel, bool auth = true, bool form = false, bool retry = true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(30)); cancel = deadline.Token;
            var uri = new Uri(url); if (!Allowed(uri)) throw new PanelApiException("网盘 API 地址超出平台范围，已停止发送凭据。");
            string json = body == null ? "" : new JavaScriptSerializer().Serialize(body);
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrEmpty(_account.UserAgent) ? "Mozilla/5.0" : _account.UserAgent);
                if (body != null) request.Content = form ? (HttpContent)new FormUrlEncodedContent(Obj(body).ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture))) : new StringContent(json, Encoding.UTF8, "application/json");
                if (Kind == PanelKind.Pan123) request.Headers.TryAddWithoutValidation("platform", "open_platform");
                if (Kind == PanelKind.Thunder)
                {
                    request.Headers.TryAddWithoutValidation("X-Device-Id", _account.DeviceId);
                    if (_account.LoginMode == "android") request.Headers.TryAddWithoutValidation("X-Client-Version", "8.31.0.9726");
                    if (_account.ClientId.Length > 0) request.Headers.TryAddWithoutValidation("X-Client-Id", _account.ClientId);
                }
                if (auth)
                {
                    if (Kind == PanelKind.Tianyi)
                    {
                        string date = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
                        string material = "SessionKey=" + _account.SessionKey + "&Operate=" + request.Method.Method + "&RequestURI=" + uri.AbsolutePath + "&Date=" + date;
                        using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_account.SessionSecret)))
                            request.Headers.TryAddWithoutValidation("Signature", Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(material))).ToUpperInvariant());
                        request.Headers.TryAddWithoutValidation("Date", date); request.Headers.TryAddWithoutValidation("SessionKey", _account.SessionKey);
                        request.Headers.TryAddWithoutValidation("X-Request-ID", Guid.NewGuid().ToString());
                    }
                    else if (Kind == PanelKind.Mobile139) MobileHeaders(request, json);
                    else request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _account.AccessToken);
                    if (Kind == PanelKind.Thunder)
                    {
                        request.Headers.TryAddWithoutValidation("X-Captcha-Token", _account.CaptchaToken);
                    }
                }
                Dictionary<string, object> result; int status;
                try
                {
                    using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                    using (cancel.Register(() => response.Dispose()))
                    {
                        status = (int)response.StatusCode;
                        if (status >= 300 && status < 400) throw new PanelApiException("网盘 API 返回重定向，已停止发送授权信息。", status: status);
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            var bytes = new byte[8192]; int n;
                            while ((n = await stream.ReadAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false)) > 0)
                            { if (buffer.Length + n > 8 * 1024 * 1024) throw new PanelApiException("网盘响应超过 8 MiB，无法完整读取。"); buffer.Write(bytes, 0, n); }
                            try { result = Obj(new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(Encoding.UTF8.GetString(buffer.ToArray()))); }
                            catch { throw new PanelApiException("网盘返回的不是有效 JSON（HTTP " + status + "）。", status: status); }
                        }
                    }
                }
                catch (HttpRequestException) { throw new PanelApiException("网盘 API 网络连接失败，请检查网络与代理。"); }
                string code = Str(result, "code"); string error = Str(result, "error");
                string ec = Str(result, "error_code");
                bool unauthorized = status == 401 || code == "401" || ec == "40140125" || ec == "40140126" ||
                    new[] { "res_code", "resCode", "errorCode" }.Any(k => Str(result, k) == "InvalidSessionKey") ||
                    new[] { "AccessTokenExpired", "AccessTokenInvalid", "InvalidSessionKey" }.Contains(code) ||
                    new[] { "4121", "4122", "10", "16" }.Contains(ec);
                if (auth && retry && unauthorized && await Renew(cancel).ConfigureAwait(false))
                    return await Send(url, body, cancel, auth, form, false).ConfigureAwait(false);
                bool failure = status < 200 || status >= 300 || result.Count == 0 ||
                    result.ContainsKey("success") && Str(result, "success").Equals("False", StringComparison.OrdinalIgnoreCase) ||
                    result.ContainsKey("state") && Str(result, "state").Equals("False", StringComparison.OrdinalIgnoreCase) ||
                    Kind == PanelKind.Pan123 && code != "0" ||
                    Kind == PanelKind.Aliyun && code.Length > 0 && code != "0" ||
                    ec.Length > 0 && ec != "0" || error.Length > 0 ||
                    Kind == PanelKind.Tianyi && new[] { "res_code", "resCode", "errorCode" }.Any(k => Str(result, k).Length > 0 && Str(result, k) != "0");
                if (failure)
                {
                    // Never print server text: it can echo tokens, signed URLs or request parameters.
                    string rawCode = ec.Length > 0 ? ec : code;
                    string safeCode = long.TryParse(rawCode, out _) || new[] { "AccessTokenExpired", "AccessTokenInvalid", "InvalidSessionKey" }.Contains(rawCode) ? rawCode : "";
                    bool captcha = Kind == PanelKind.Thunder && ec == "9";
                    throw new PanelApiException(PanelRegistry.Describe(Kind).DisplayName + "请求失败（HTTP " + status + (safeCode.Length > 0 ? "，代码 " + safeCode : "") + "）。" +
                        (captcha ? "请在官方页面完成验证后更新 Captcha Token。" : unauthorized ? "授权已失效，请重新导入凭据或核对应用授权信息。" : "请检查接口权限、账号权益及授权状态；平台限流时请稍后重试。"), status: status, needsCaptcha: captcha);
                }
                return result;
            }
        }
        private static string Hex(byte[] value) => string.Concat(value.Select(b => b.ToString("x2")));
        private static string Md5(string value) { using (var md5 = MD5.Create()) return Hex(md5.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        private void MobileHeaders(HttpRequestMessage request, string body)
        {
            string ts = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string random = Guid.NewGuid().ToString("N").Substring(0, 16);
            string escaped = Uri.EscapeDataString(body).Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");
            string sorted = new string(escaped.OrderBy(c => c).ToArray());
            string sign = Md5(Md5(Convert.ToBase64String(Encoding.UTF8.GetBytes(sorted))) + Md5(ts + ":" + random)).ToUpperInvariant();
            var headers = new Dictionary<string, string> {
                { "Authorization", "Basic " + _account.Authorization }, { "Origin", "https://yun.139.com" }, { "Referer", "https://yun.139.com/w/" }, { "Caller", "web" }, { "Cms-Device", "default" },
                { "Mcloud-Channel", "1000101" }, { "Mcloud-Client", "10701" }, { "Mcloud-Route", "001" },
                { "Mcloud-Sign", ts + "," + random + "," + sign }, { "Mcloud-Version", "7.14.0" },
                { "x-DeviceInfo", "||9|7.14.0|chrome|120.0.0.0|||windows 10||zh-CN|||" },
                { "x-huawei-channelSrc", "10000034" }, { "x-inner-ntwk", "2" }, { "x-m4c-caller", "PC" },
                { "x-m4c-src", "10002" }, { "x-SvcType", "1" }, { "X-Yun-Api-Version", "v1" },
                { "X-Yun-App-Channel", "10000034" }, { "X-Yun-Channel-Source", "10000034" },
                { "X-Yun-Client-Info", "||9|7.14.0|chrome|120.0.0.0|||windows 10||zh-CN|||dW5kZWZpbmVk||" },
                { "X-Yun-Module-Type", "100" }, { "X-Yun-Svc-Type", "1" } };
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
        private async Task<bool> Renew(CancellationToken cancel)
        {
            Dictionary<string, object> response;
            if (Kind == PanelKind.Aliyun && _account.RefreshToken.Length > 0 && _account.ClientId.Length > 0 && _account.ClientSecret.Length > 0)
                response = await Send(Ali + "/oauth/access_token", Map("client_id", _account.ClientId, "client_secret", _account.ClientSecret, "grant_type", "refresh_token", "refresh_token", _account.RefreshToken), cancel, false).ConfigureAwait(false);
            else if (Kind == PanelKind.Pan115 && _account.RefreshToken.Length > 0)
                response = Obj(At(await Send("https://passportapi.115.com/open/refreshToken", Map("refresh_token", _account.RefreshToken), cancel, false, true).ConfigureAwait(false), "data"));
            else if (Kind == PanelKind.Pan123 && _account.ClientId.Length > 0 && _account.ClientSecret.Length > 0)
                response = Obj(At(await Send(P123 + "/api/v1/access_token", Map("clientID", _account.ClientId, "clientSecret", _account.ClientSecret), cancel, false).ConfigureAwait(false), "data"));
            else if (Kind == PanelKind.Thunder && _account.RefreshToken.Length > 0 && _account.ClientId.Length > 0 && _account.ClientSecret.Length > 0)
                response = await Send(Xauth + "/auth/token", Map("grant_type", "refresh_token", "refresh_token", _account.RefreshToken, "client_id", _account.ClientId, "client_secret", _account.ClientSecret), cancel, false).ConfigureAwait(false);
            else if (Kind == PanelKind.Tianyi && _account.AccessToken.Length > 0)
            {
                response = await Send(Tian + "/getSessionForPC.action?" + Query("clientType", "TELEPC", "version", "7.2.4.0", "channelId", "web_cloud.189.cn", "appId", "9317140619", "accessToken", _account.AccessToken), null, cancel, false).ConfigureAwait(false);
                if (Str(response, "sessionKey").Length == 0 || Str(response, "sessionSecret").Length == 0) throw new PanelApiException("天翼云盘未返回有效 PC 会话，请重新授权。");
                _account.SessionKey = Str(response, "sessionKey"); _account.SessionSecret = Str(response, "sessionSecret");
                CredentialsChanged?.Invoke(); return true;
            }
            else return false;
            string token = Str(response, Kind == PanelKind.Pan123 ? "accessToken" : "access_token");
            if (token.Length == 0) throw new PanelApiException("网盘授权接口未返回有效令牌，请重新授权。");
            _account.AccessToken = token;
            if (Str(response, "refresh_token").Length > 0) _account.RefreshToken = Str(response, "refresh_token");
            CredentialsChanged?.Invoke(); return true;
        }
        private async Task Ensure(CancellationToken cancel)
        {
            if (Kind == PanelKind.Tianyi && _account.SessionKey.Length == 0 || Kind != PanelKind.Tianyi && Kind != PanelKind.Mobile139 && _account.AccessToken.Length == 0)
                if (!await Renew(cancel).ConfigureAwait(false)) throw new PanelApiException("请填写有效授权信息。");
            if (Kind == PanelKind.Aliyun && _account.DriveId.Length == 0) await AliInfo(cancel).ConfigureAwait(false);
            if (Kind == PanelKind.Mobile139 && _mobileHost == null) await MobileInfo(cancel).ConfigureAwait(false);
        }
        private async Task<Dictionary<string, object>> AliInfo(CancellationToken cancel)
        {
            var info = await Send(Ali + "/adrive/v1.0/user/getDriveInfo", Map(), cancel).ConfigureAwait(false);
            _account.DriveId = Str(info, "default_drive_id");
            if (_account.DriveId.Length == 0) _account.DriveId = Str(info, "resource_drive_id");
            if (_account.DriveId.Length == 0) throw new PanelApiException("阿里云盘未返回可访问的云盘 ID，请核对应用权限。");
            return info;
        }
        private async Task MobileInfo(CancellationToken cancel)
        {
            _account.Authorization = _account.Authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ? _account.Authorization.Substring(6).Trim() : _account.Authorization;
            string[] parts;
            try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(_account.Authorization)).Split(':'); }
            catch { throw new PanelApiException("中国移动云盘 Authorization 必须是有效的 Base64 授权值。"); }
            if (parts.Length < 3 || parts[1].Length == 0) throw new PanelApiException("中国移动云盘授权信息缺少账号，请从官方网页重新获取。");
            var route = await Send("https://user-njs.yun.139.com/user/route/qryRoutePolicy", Map("userInfo", Map("userType", 1, "accountType", 1, "accountName", parts[1]), "modAddrType", 1), cancel).ConfigureAwait(false);
            string host = Arr(At(route, "data", "routePolicyList")).Where(p => Str(p, "modName") == "personal").Select(p => Str(p, "httpsUrl")).FirstOrDefault();
            if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || !MobileOrigin(uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new PanelApiException("中国移动云盘个人云路由无效或超出官方域名范围。");
            _mobileHost = uri.AbsoluteUri.TrimEnd('/'); _account.Identity = parts[1];
        }
        public Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default) => Locked(async ct =>
        {
            await Ensure(ct).ConfigureAwait(false); object info; string id, name;
            switch (Kind)
            {
                case PanelKind.Aliyun: info = await AliInfo(ct).ConfigureAwait(false); id = Str(info, "user_id"); name = Str(info, "name"); break;
                case PanelKind.Pan123: info = At(await Send(P123 + "/api/v1/user/info", null, ct).ConfigureAwait(false), "data"); id = Str(info, "uid"); name = Str(info, "nickname"); break;
                case PanelKind.Pan115: info = At(await Send(P115 + "/open/user/info", null, ct).ConfigureAwait(false), "data"); id = Str(info, "user_id"); name = Str(info, "user_name"); break;
                case PanelKind.Thunder: info = await Send(Xauth + "/user/me", null, ct).ConfigureAwait(false); id = Str(info, "sub"); if (id.Length == 0) id = Str(info, "user_id"); name = Str(info, "name"); break;
                case PanelKind.Tianyi:
                    info = await Send(Tian + "/getUserInfo.action?" + Query("clientType", "TELEPC", "version", "7.2.4.0"), null, ct).ConfigureAwait(false);
                    id = Str(info, "userId"); if (id.Length == 0) id = Str(info, "loginName"); if (id.Length == 0) id = Str(info, "userAccount");
                    name = Str(info, "nickName"); break;
                default:
                    // Route validation alone is insufficient; require a successful directory query.
                    await ListPage("/", "", 1, ct).ConfigureAwait(false); id = _account.Identity; name = "中国移动云盘账号"; break;
            }
            if (id.Length == 0) throw new PanelApiException("网盘未返回账号身份，无法安全记忆和切换账号。");
            _account.Identity = id;
            return (true, _account.Name.Length > 0 ? _account.Name : name.Length > 0 ? name : PanelRegistry.Describe(Kind).DisplayName + "账号");
        }, cancel);
        private string Root => Kind == PanelKind.Aliyun ? "root" : Kind == PanelKind.Tianyi ? "-11" : Kind == PanelKind.Mobile139 ? "/" : Kind == PanelKind.Thunder ? "" : "0";
        public Task<List<QuarkFileEntry>> ListDirAsync(string directory, int maxPages, CancellationToken cancel = default) => Locked(async ct =>
        {
            if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));
            await Ensure(ct).ConfigureAwait(false); string dir = string.IsNullOrEmpty(directory) || directory == "0" ? Root : directory;
            var files = new List<QuarkFileEntry>(); var ids = new HashSet<string>(); var cursors = new HashSet<string>(); string cursor = "";
            for (int page = 1; page <= maxPages; page++)
            {
                var response = await ListPage(dir, cursor, page, ct).ConfigureAwait(false);
                foreach (var item in response.files) if (ids.Add(item.Fid)) files.Add(item);
                if (response.next == null) return files;
                if (!cursors.Add(response.next)) throw new PanelApiException("网盘分页游标重复，目录读取已停止，未返回不完整目录。");
                cursor = response.next;
            }
            throw new PanelApiException("目录超过分页上限，未返回不完整列表；请提高分页上限后重试。");
        }, cancel);
        private async Task<(List<QuarkFileEntry> files, string next)> ListPage(string dir, string cursor, int page, CancellationToken cancel)
        {
            object response, items; string next; int limit = Kind == PanelKind.Aliyun ? 200 : 100;
            switch (Kind)
            {
                case PanelKind.Aliyun:
                    response = await Send(Ali + "/adrive/v1.0/openFile/list", Map("drive_id", _account.DriveId, "parent_file_id", dir, "limit", limit, "marker", cursor, "order_by", "name", "order_direction", "ASC"), cancel).ConfigureAwait(false);
                    items = At(response, "items"); next = Str(response, "next_marker"); break;
                case PanelKind.Pan123:
                    response = At(await Send(P123 + "/api/v2/file/list?" + Query("parentFileId", dir, "limit", limit, "lastFileId", cursor.Length == 0 ? "0" : cursor, "trashed", "false"), null, cancel).ConfigureAwait(false), "data");
                    items = At(response, "fileList"); next = Str(response, "lastFileId"); if (next == "-1" || next == "0") next = ""; break;
                case PanelKind.Pan115:
                    response = await Send(P115 + "/open/ufile/files?" + Query("cid", dir, "limit", limit, "offset", (page - 1) * limit, "show_dir", 1, "cur", 1, "o", "file_name", "asc", 1), null, cancel).ConfigureAwait(false);
                    items = At(response, "data"); next = page * limit < Num(response, "count") ? page.ToString() : ""; break;
                case PanelKind.Tianyi:
                    response = At(await Send(Tian + "/listFiles.action?" + Query("clientType", "TELEPC", "version", "7.2.4.0", "channelId", "web_cloud.189.cn", "folderId", dir, "fileType", 0, "mediaAttr", 0, "iconOption", 5, "pageNum", page, "pageSize", limit, "recursive", 0, "orderBy", "filename", "descending", "false"), null, cancel).ConfigureAwait(false), "fileListAO");
                    var folders = Arr(At(response, "folderList")).Select(f => { var copy = new Dictionary<string, object>(Obj(f)); copy["zeraFolder"] = true; return (object)copy; });
                    items = folders.Concat(Arr(At(response, "fileList"))).ToArray(); next = Arr(items).Count() >= limit ? page.ToString() : ""; break;
                case PanelKind.Mobile139:
                    response = At(await Send(_mobileHost + "/file/list", Map("imageThumbnailStyleList", new[] { "Small", "Large" }, "orderBy", "updated_at", "orderDirection", "DESC", "pageInfo", Map("pageCursor", cursor, "pageSize", limit), "parentFileId", dir), cancel).ConfigureAwait(false), "data");
                    items = At(response, "items"); next = Str(response, "nextPageCursor"); break;
                default:
                    response = await Send(Xun + "/files?" + Query("parent_id", dir, "page_token", cursor, "limit", limit, "__type", "drive", "refresh", "true", "__sync", "true", "with_audit", "true", "filters", "{\"phase\":{\"eq\":\"PHASE_TYPE_COMPLETE\"},\"trashed\":{\"eq\":false}}"), null, cancel).ConfigureAwait(false);
                    items = At(response, "files"); next = Str(response, "next_page_token"); break;
            }
            if (!(items is IEnumerable) || items is string || items is IDictionary) throw new PanelApiException("网盘目录响应缺少有效文件列表，未将异常响应视为空目录。");
            var result = new List<QuarkFileEntry>();
            foreach (var file in Arr(items))
            {
                if (Kind == PanelKind.Pan123 && Num(file, "trashed") != 0) continue;
                string id, name; long size; bool folder;
                switch (Kind)
                {
                    case PanelKind.Aliyun: id = Str(file, "file_id"); name = Str(file, "name"); size = Num(file, "size"); folder = Str(file, "type") == "folder"; break;
                    case PanelKind.Pan123: id = Str(file, "fileId"); name = Str(file, "filename"); size = Num(file, "size"); folder = Num(file, "type") == 1; break;
                    case PanelKind.Pan115: id = Str(file, "fid"); name = Str(file, "fn"); size = Num(file, "fs"); folder = Str(file, "fc") == "0"; if (!folder) id += "|" + Str(file, "pc"); break;
                    case PanelKind.Tianyi: id = Str(file, "id"); name = Str(file, "name").Replace("\\'", "'"); size = Num(file, "size"); folder = At(file, "zeraFolder") != null; break;
                    case PanelKind.Mobile139: id = Str(file, "fileId"); name = Str(file, "name"); size = Num(file, "size"); folder = Str(file, "type") == "folder"; break;
                    default: id = Str(file, "id"); name = Str(file, "name"); size = Num(file, "size"); folder = Str(file, "kind") == "drive#folder"; break;
                }
                if (id.Length == 0 || name.Length == 0) throw new PanelApiException("网盘返回的文件条目缺少 ID 或名称。");
                result.Add(new QuarkFileEntry { Fid = id, Name = name, Size = Math.Max(0, size), IsDir = folder });
            }
            return (result, next.Length == 0 ? null : next);
        }
        public Task<DownloadLink> ResolveDriveFileAsync(string fileId, CancellationToken cancel = default) => Locked(async ct =>
        {
            if (string.IsNullOrWhiteSpace(fileId)) throw new ArgumentException("文件 ID 不能为空。");
            await Ensure(ct).ConfigureAwait(false); object response; string url;
            switch (Kind)
            {
                case PanelKind.Aliyun:
                    response = await Send(Ali + "/adrive/v1.0/openFile/getDownloadUrl", Map("drive_id", _account.DriveId, "file_id", fileId, "expire_sec", 14400), ct).ConfigureAwait(false); url = Str(response, "url"); break;
                case PanelKind.Pan123:
                    response = await Send(P123 + "/api/v1/file/download_info?" + Query("fileId", fileId), null, ct).ConfigureAwait(false); url = Str(response, "data", "downloadUrl"); break;
                case PanelKind.Pan115:
                    var parts = fileId.Split('|');
                    string pick = parts.Length == 2 ? parts[1] : "";
                    if (pick.Length == 0) throw new PanelApiException("115 文件缺少提取码，请重新浏览目录后创建下载任务。");
                    response = At(await Send(P115 + "/open/ufile/downurl", Map("pick_code", pick), ct, true, true).ConfigureAwait(false), "data");
                    url = Str(At(response, parts[0]), "url", "url"); break;
                case PanelKind.Tianyi:
                    response = await Send(Tian + "/getFileDownloadUrl.action?" + Query("clientType", "TELEPC", "version", "7.2.4.0", "fileId", fileId, "dt", 3, "flag", 1), null, ct).ConfigureAwait(false); url = Str(response, "fileDownloadUrl").Replace("&amp;", "&"); break;
                case PanelKind.Mobile139:
                    response = At(await Send(_mobileHost + "/file/getDownloadUrl", Map("fileId", fileId), ct).ConfigureAwait(false), "data");
                    url = Str(response, "cdnSwitch").Equals("True", StringComparison.OrdinalIgnoreCase) && Str(response, "cdnUrl").Length > 0 ? Str(response, "cdnUrl") : Str(response, "url"); break;
                default:
                    response = await Send(Xun + "/files/" + Uri.EscapeDataString(fileId), null, ct).ConfigureAwait(false); url = Str(response, "web_content_link"); break;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" && uri.Scheme != "http" || uri.UserInfo.Length > 0)
                throw new PanelApiException("网盘未返回有效的 HTTP(S) 原文件下载地址，请检查文件状态与下载权限。");
            return new DownloadLink { Fid = fileId, DownloadUrl = uri.AbsoluteUri, AccountKey = AccountKey };
        }, cancel);
        public async Task<string> RefreshLinkAsync(Download.PendingInfo pending, CancellationToken cancel = default) => (await ResolveDriveFileAsync(pending.Fid, cancel).ConfigureAwait(false)).DownloadUrl;
        public DownloadRequestContext BuildDownloadContext() => new DownloadRequestContext { Cookie = "", Authorization = "", Referer = "", Origin = "", UserAgent = _account.DownloadUserAgent.Length > 0 ? _account.DownloadUserAgent : _account.UserAgent.Length > 0 ? _account.UserAgent : "Mozilla/5.0", SuggestedThreads = 2, MaxRequestBytes = 4 * 1024 * 1024 };
        public Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default) => throw new NotSupportedException("此原生接入暂未提供容量查询。");
        public Task<List<DownloadLink>> ResolveShareAsync(string url, string passcode, int maxItems, CancellationToken cancel = default) => throw new NotSupportedException("此原生接入仅支持本人网盘目录下载，暂不解析分享链接。");
        public void ShutdownPlatformResources() { }
        public void Dispose() => _http.Dispose();
    }
}
