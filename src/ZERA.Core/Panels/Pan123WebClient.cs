using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using ZERA.Core.Models;

namespace ZERA.Core.Panels
{
    // Website authentication has a different token and API from the 123 open platform.
    public sealed class Pan123WebClient : IPanelClient
    {
        private readonly NativeCloudAccount _account;
        private readonly HttpClient _http;
        private readonly HttpClient _downloadHttp;
        private readonly ConcurrentDictionary<string, string> _referers = new ConcurrentDictionary<string, string>();
        private const string Api = "https://yun.123pan.com/b/api";
        public PanelKind Kind => PanelKind.Pan123;
        public bool IsLoggedIn => _account.AccessToken.Length > 0;
        public string CookieStr => _account.Serialize();
        public string AccountKey => _account.Identity.Length == 0 ? null : PanelAdapter.CredentialKey("Pan123Web_", _account.Identity);
        public Pan123WebClient(string credential, HttpMessageHandler handler = null, HttpMessageHandler downloadHandler = null)
        {
            _account = NativeCloudAccount.Parse(credential); _account.Validate(Kind);
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            _downloadHttp = new HttpClient(downloadHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        }
        private static object At(object value, params string[] path) { foreach (var key in path) if (!(value is Dictionary<string, object> map) || !map.TryGetValue(key, out value)) return null; return value; }
        private static string S(object value, params string[] path) => Convert.ToString(At(value, path), CultureInfo.InvariantCulture) ?? "";
        private static long N(object value, params string[] path) => long.TryParse(S(value, path), out long n) ? n : 0;
        private static string Q(params string[] values) { var result = new List<string>(); for (int i = 0; i < values.Length; i += 2) result.Add(Uri.EscapeDataString(values[i]) + "=" + Uri.EscapeDataString(values[i + 1])); return string.Join("&", result); }
        public static uint Crc32(string text)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in Encoding.UTF8.GetBytes(text)) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u); }
            return ~crc;
        }
        private static string Sign(Uri uri)
        {
            string table = "adefghlmyijnopkqrstubcvwsz";
            var now = DateTimeOffset.UtcNow; string minute = now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            string key = Crc32(new string(minute.Select(c => table[c - '0']).ToArray())).ToString(CultureInfo.InvariantCulture);
            string timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            // The web signature nonce is a decimal integer.
            string random = (BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0) % 10000001).ToString(CultureInfo.InvariantCulture);
            string sign = Crc32(string.Join("|", timestamp, random, uri.AbsolutePath, "web", "3", key)).ToString(CultureInfo.InvariantCulture);
            return key + "=" + timestamp + "-" + random + "-" + sign;
        }
        private async Task<object> Request(string path, object body, CancellationToken cancel)
        {
            Uri uri = new Uri(Api + path);
            Require(uri.Scheme == "https" && uri.Host == "yun.123pan.com" && uri.AbsolutePath.StartsWith("/b/api/", StringComparison.Ordinal), "123 请求超出 API 范围。");
            string url = uri.AbsoluteUri + (uri.Query.Length == 0 ? "?" : "&") + Sign(uri);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _account.AccessToken);
                request.Headers.TryAddWithoutValidation("User-Agent", _account.UserAgent);
                request.Headers.TryAddWithoutValidation("platform", "web"); request.Headers.TryAddWithoutValidation("app-version", "3");
                request.Headers.Referrer = new Uri("https://yun.123pan.com/"); request.Headers.TryAddWithoutValidation("Origin", "https://yun.123pan.com");
                if (body != null) request.Content = new StringContent(new JavaScriptSerializer().Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                using (deadline.Token.Register(() => response.Dispose()))
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memory = new MemoryStream())
                {
                    Require(response.IsSuccessStatusCode, "123 网页接口请求失败（HTTP " + (int)response.StatusCode + "），请重新登录或检查下载权限。");
                    var buffer = new byte[8192]; int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                    { Require(memory.Length + n <= 8 * 1024 * 1024, "123 响应过大。"); memory.Write(buffer, 0, n); }
                    object value;
                    try { value = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(Encoding.UTF8.GetString(memory.ToArray())); }
                    catch { throw new PanelApiException("123 接口响应格式无效。"); }
                    Require(S(value, "code") == "0", S(value, "code") == "401" ? "123 网页授权已过期，请重新密码登录。" : "123 网页接口未通过，请检查验证要求、账号权益或稍后重试。"); return value;
                }
            }
        }
        private static void Require(bool value, string message) { if (!value) throw new PanelApiException(message); }
        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            var info = At(await Request("/user/info", null, cancel).ConfigureAwait(false), "data");
            string id = S(info, "UID"); Require(id.Length > 0, "123 未返回账号身份，不能记忆此登录态。"); _account.Identity = id;
            return (true, _account.Name.Length > 0 ? _account.Name : S(info, "Nickname"));
        }
        public async Task<List<QuarkFileEntry>> ListDirAsync(string dir, int maxPages, CancellationToken cancel = default)
        {
            if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages));
            var files = new List<QuarkFileEntry>(); var seen = new HashSet<string>();
            for (int page = 1; page <= maxPages; page++)
            {
                var value = At(await Request("/file/list/new?" + Q("driveId", "0", "limit", "100", "next", "0", "orderBy", "file_id", "orderDirection", "desc", "parentFileId", string.IsNullOrEmpty(dir) ? "0" : dir, "trashed", "false", "Page", page.ToString(CultureInfo.InvariantCulture), "SearchData", "", "OnlyLookAbnormalFile", "0", "event", "homeListFile", "operateType", "4", "inDirectSpace", "false"), null, cancel).ConfigureAwait(false), "data");
                object items = At(value, "InfoList"); Require(items is IEnumerable && !(items is string) && !(items is IDictionary), "123 响应缺少有效目录列表。"); int count = 0;
                foreach (object item in (IEnumerable)items)
                {
                    count++; string id = S(item, "FileId"); Require(id.Length > 0 && S(item, "FileName").Length > 0, "123 文件条目缺少标识。");
                    if (!seen.Add(id)) throw new PanelApiException("123 返回重复分页，未使用不完整目录。");
                    bool folder = N(item, "Type") == 1;
                    var metadata = new Dictionary<string, object> { { "driveId", 0 }, { "etag", S(item, "Etag") }, { "fileId", id }, { "fileName", S(item, "FileName") }, { "s3keyFlag", S(item, "S3KeyFlag") }, { "size", N(item, "Size") }, { "type", 0 } };
                    string fid = folder ? id : "web123:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(metadata)));
                    files.Add(new QuarkFileEntry { Fid = fid, Name = S(item, "FileName"), IsDir = folder, Size = Math.Max(0, N(item, "Size")) });
                }
                if (count == 0 || S(value, "Next") == "-1" || count < 100) return files;
            }
            throw new PanelApiException("123 目录超过分页上限，未返回不完整结果。");
        }
        public async Task<DownloadLink> ResolveDriveFileAsync(string id, CancellationToken cancel = default)
        {
            object metadata;
            try { if (id == null || !id.StartsWith("web123:") || id.Length > 16384) throw new ArgumentException(); metadata = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(Convert.FromBase64String(id.Substring(7)))); }
            catch { throw new PanelApiException("123 网页任务缺少文件元数据，请重新选择文件创建下载。"); }
            string url = S(await Request("/file/download_info", metadata, cancel).ConfigureAwait(false), "data", "DownloadUrl");
            Require(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && uri.UserInfo.Length == 0, "123 未返回下载地址。");
            string referer = uri.GetLeftPart(UriPartial.Authority) + "/";
            string encoded = HttpUtility.ParseQueryString(uri.Query)["params"];
            if (!string.IsNullOrEmpty(encoded)) try { url = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); } catch { throw new PanelApiException("123 下载地址编码无效。"); }
            Require(Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http") && uri.UserInfo.Length == 0, "123 未返回有效原文件地址。");
            // The web API may return a JSON/redirect gateway instead of file bytes.
            url = await ResolveGateway(uri, referer, cancel).ConfigureAwait(false); _referers[id] = referer;
            return new DownloadLink { Fid = id, DownloadUrl = url, AccountKey = AccountKey };
        }
        private async Task<string> ResolveGateway(Uri uri, string referer, CancellationToken cancel)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30)); request.Headers.Referrer = new Uri(referer); request.Headers.TryAddWithoutValidation("User-Agent", _account.UserAgent);
                // This client has no Authorization or cookie defaults; API tokens never leave the API origin.
                using (var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                using (deadline.Token.Register(() => response.Dispose()))
                {
                    int status = (int)response.StatusCode; string target = null;
                    if (status >= 300 && status < 400) target = response.Headers.Location == null ? null : new Uri(uri, response.Headers.Location).AbsoluteUri;
                    else
                    {
                        Require(response.IsSuccessStatusCode, "123 下载网关拒绝请求（HTTP " + status + "），请检查账号下载权益或稍后重试。");
                        string type = response.Content.Headers.ContentType?.MediaType ?? "";
                        Require(type != "text/html", "123 返回了网页而非文件，可能需要额外验证或下载权限。");
                        if (type.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0) return uri.AbsoluteUri;
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var memory = new MemoryStream())
                        {
                            var bytes = new byte[4096]; int n;
                            while ((n = await stream.ReadAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false)) > 0) { Require(memory.Length + n <= 256 * 1024, "123 下载网关响应过大。"); memory.Write(bytes, 0, n); }
                            object value;
                            try { value = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(memory.ToArray())); } catch { throw new PanelApiException("123 下载网关响应格式无效。"); }
                            target = S(value, "data", "redirect_url");
                        }
                    }
                    Require(Uri.TryCreate(target, UriKind.Absolute, out var final) && (final.Scheme == "https" || final.Scheme == "http") && final.UserInfo.Length == 0, "123 下载网关未返回有效文件地址，请检查验证和权益要求。");
                    return final.AbsoluteUri;
                }
            }
        }
        public async Task<string> RefreshLinkAsync(Download.PendingInfo pending, CancellationToken cancel = default) => (await ResolveDriveFileAsync(pending.Fid, cancel).ConfigureAwait(false)).DownloadUrl;
        public DownloadRequestContext BuildDownloadContext() => BuildDownloadContext(null);
        public DownloadRequestContext BuildDownloadContext(string fileId) => new DownloadRequestContext { Cookie = "", Authorization = "", UserAgent = _account.UserAgent, Referer = fileId != null && _referers.TryGetValue(fileId, out var value) ? value : "https://yun.123pan.com/", Origin = "", SuggestedThreads = 2, MaxRequestBytes = 4 * 1024 * 1024 };
        public Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default) => throw new NotSupportedException("暂未提供容量查询。");
        public Task<List<DownloadLink>> ResolveShareAsync(string url, string passcode, int maxItems, CancellationToken cancel = default) => throw new NotSupportedException("此登录方式暂不解析分享链接。");
        public void ShutdownPlatformResources() { }
        public void Dispose() { _http.Dispose(); _downloadHttp.Dispose(); }
    }
}
