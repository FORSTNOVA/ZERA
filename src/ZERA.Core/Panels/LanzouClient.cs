using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ZERA.Core.Models;

namespace ZERA.Core.Panels
{
    // Public share pages only. No account cookie, script execution, or third-party parsing service.
    public sealed class LanzouClient : IPanelClient
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
        private readonly CookieContainer _cookies = new CookieContainer();
        private string _root;
        private string _password;
        private const string Prefix = "lanzou:";
        private static readonly Regex UrlPattern = new Regex(@"https?://[^\s""'<>，。；：）】\]]+", RegexOptions.IgnoreCase);
        private static Match Match(string value, string pattern) => Regex.Match(value ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        private static MatchCollection Matches(string value, string pattern) => Regex.Matches(value ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        public static bool IsShareHost(string host) => Regex.IsMatch(host ?? "", @"^(?:[a-z0-9-]+\.)*(?:lanzou[a-z]?|lanzn)\.com$", RegexOptions.IgnoreCase);
        public static bool TryParseShare(string text, out string url, out string password)
        {
            url = null; password = "";
            foreach (System.Text.RegularExpressions.Match found in UrlPattern.Matches(text ?? ""))
            {
                if (!Uri.TryCreate(found.Value, UriKind.Absolute, out var uri) || !IsShareHost(uri.Host) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
                    || !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_-]+/?$")) continue;
                url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                var code = Match(uri.Query, @"[?&](?:pwd|password)=([^&#]+)");
                if (code.Success) password = Uri.UnescapeDataString(code.Groups[1].Value);
                else
                {
                    code = Match(text, @"(?:提取码|提取碼|密码|密碼|访问码)\s*[:：=]?\s*([A-Za-z0-9]{1,32})");
                    if (code.Success) password = code.Groups[1].Value;
                }
                return true;
            }
            return false;
        }
        public LanzouClient(HttpMessageHandler handler = null)
        {
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(30) };
        }
        public PanelKind Kind => PanelKind.Lanzou;
        public bool IsLoggedIn => false;
        public string CookieStr => "";
        public Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default) => Task.FromResult((false, "蓝奏云分享无需登录"));
        public Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default) => throw new NotSupportedException("蓝奏云仅支持分享下载。");
        public void ShutdownPlatformResources() { }
        public void Dispose() => _http.Dispose();
        public DownloadRequestContext BuildDownloadContext() => new DownloadRequestContext { UserAgent = UserAgent, Cookie = "down_ip=1", Origin = "", Referer = "", SuggestedThreads = 2, MaxRequestBytes = 4 * 1024 * 1024 };

        private static Uri ShareUri(string value)
        {
            if (!TryParseShare(value, out var url, out _) || url != value.TrimEnd('/')) throw new PanelApiException("不是受支持的蓝奏云分享地址；暂不支持蓝奏优享。 ");
            return new Uri(url);
        }
        private string Encode(Uri uri, string password) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(_json.Serialize(new[] { uri.AbsoluteUri, password ?? "" })));
        private (Uri uri, string password) Decode(string id)
        {
            try
            {
                if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length > 8192) throw new FormatException();
                var values = _json.Deserialize<string[]>(Encoding.UTF8.GetString(Convert.FromBase64String(id.Substring(Prefix.Length))));
                if (values.Length != 2) throw new FormatException();
                return (ShareUri(values[0]), values[1] ?? "");
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidOperationException)
            { throw new PanelApiException("蓝奏云任务的分享信息不完整，请重新解析分享链接。"); }
        }
        public async Task OpenShareAsync(string url, string password, CancellationToken cancel = default)
        {
            var uri = ShareUri(url);
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try { cancel.ThrowIfCancellationRequested(); _root = uri.AbsoluteUri; _password = password ?? ""; }
            finally { _gate.Release(); }
        }
        public async Task<List<QuarkFileEntry>> ListDirAsync(string id, int maxPages, CancellationToken cancel = default)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                var source = string.IsNullOrEmpty(id) || id == "0" ? (ShareUri(_root ?? ""), _password) : Decode(id);
                return await List(source.Item1, source.Item2, Math.Max(1, Math.Min(maxPages, 100)), cancel).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        public async Task<List<DownloadLink>> ResolveShareAsync(string urlOrCode, string passcode, int maxItems, CancellationToken cancel = default)
        {
            if (maxItems <= 0) return new List<DownloadLink>();
            if (!TryParseShare(urlOrCode, out var url, out var code)) throw new PanelApiException("请输入完整的蓝奏云分享链接。");
            await OpenShareAsync(url, string.IsNullOrEmpty(passcode) ? code : passcode, cancel).ConfigureAwait(false);
            var entries = await ListDirAsync("0", 40, cancel).ConfigureAwait(false);
            var result = new List<DownloadLink>();
            foreach (var file in entries.Where(e => !e.IsDir).Take(maxItems)) result.Add(await ResolveDriveFileAsync(file.Fid, cancel).ConfigureAwait(false));
            return result;
        }
        public async Task<DownloadLink> ResolveDriveFileAsync(string fileId, CancellationToken cancel = default)
        {
            var source = Decode(fileId);
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try { return await Resolve(source.uri, source.password, fileId, cancel).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        public async Task<string> RefreshLinkAsync(Download.PendingInfo pending, CancellationToken cancel = default)
            => (await ResolveDriveFileAsync(pending.Fid, cancel).ConfigureAwait(false)).DownloadUrl;

        private async Task<List<QuarkFileEntry>> List(Uri uri, string password, int maxPages, CancellationToken cancel)
        {
            var loadedPage = await ReadPage(uri, cancel).ConfigureAwait(false);
            string html = loadedPage.html; uri = loadedPage.uri;
            if (!IsFolder(html))
            {
                string id = Encode(uri, password);
                var file = await Resolve(uri, password, id, cancel, html).ConfigureAwait(false);
                return new List<QuarkFileEntry> { new QuarkFileEntry { Fid = id, Name = file.Name, Size = file.Size, Token = "lanzou" } };
            }
            var result = new List<QuarkFileEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match folder in Matches(html, @"(?:class\s*=\s*['""][^'""]*(?:folderlink|mbxfolder)[^'""]*['""])[^>]*(?:>\s*<a\b[^>]*)?href\s*=\s*['""](/[A-Za-z0-9_-]+)['""][^>]*>(.*?)</a>"))
            {
                var child = new Uri(uri, folder.Groups[1].Value); ShareUri(child.AbsoluteUri);
                string fid = Encode(child, "");
                if (seen.Add(fid)) result.Add(new QuarkFileEntry { Fid = fid, Name = Plain(folder.Groups[2].Value), IsDir = true });
            }
            var form = Form(html); form["pwd"] = password ?? "";
            if (!form.ContainsKey("fid")) throw Changed();
            for (int page = 1; page <= maxPages; page++)
            {
                form["pg"] = page.ToString(CultureInfo.InvariantCulture);
                var data = await Post(new Uri(uri, "/filemoreajax.php"), form, uri, cancel).ConfigureAwait(false);
                string status = Value(data, "zt");
                if (status == "2") break; // Folder listing exhausted.
                EnsureSuccess(data);
                if (!data.TryGetValue("text", out var raw) || !(raw is IEnumerable rows) || raw is string) throw Changed();
                int count = 0, added = 0;
                foreach (var row in rows)
                {
                    if (!(row is Dictionary<string, object> file)) throw Changed();
                    count++;
                    string code = Value(file, "id");
                    if (!Regex.IsMatch(code, @"^[A-Za-z0-9_-]+$")) throw Changed();
                    var fileUri = new Uri(uri, "/" + code);
                    string fid = Encode(fileUri, password);
                    if (seen.Add(fid)) { added++; result.Add(new QuarkFileEntry { Fid = fid, Name = Plain(Value(file, "name_all")), Size = ParseSize(Value(file, "size")), Token = "lanzou" }); }
                }
                if (count == 0) break;
                if (added == 0) throw new PanelApiException("蓝奏云重复返回同一页，已停止列目录，请稍后重试。");
                if (page == maxPages) throw new PanelApiException("蓝奏云文件夹超过本次列目录上限，请使用更小的分享文件夹。");
                await Task.Delay(300, cancel).ConfigureAwait(false);
            }
            return result;
        }
        private async Task<DownloadLink> Resolve(Uri uri, string password, string id, CancellationToken cancel, string page = null)
        {
            string html = page;
            if (html == null) { var loaded = await ReadPage(uri, cancel).ConfigureAwait(false); html = loaded.html; uri = loaded.uri; }
            if (IsFolder(html)) throw new PanelApiException("请选择分享文件夹内的文件下载。");
            bool protectedFile = Match(html, @"pwdload|passwddiv").Success;
            if (protectedFile && string.IsNullOrEmpty(password)) throw new PanelApiException("该蓝奏云分享需要提取码，请填写后重新解析。");
            Uri pageUri = uri; string script = html;
            if (!protectedFile)
            {
                var iframe = Match(html, @"<iframe\b[^>]*\bsrc\s*=\s*['""]([^'""]+)['""]");
                if (!iframe.Success) throw Changed();
                pageUri = new Uri(uri, WebUtility.HtmlDecode(iframe.Groups[1].Value)); CheckPage(pageUri);
                var frame = await ReadPage(pageUri, cancel).ConfigureAwait(false); script = frame.html; pageUri = frame.uri;
            }
            var endpoint = Match(script, @"['""]([^'""]*ajax(?:m|file)\.php(?:\?[^'""]*)?)['""]");
            if (!endpoint.Success) throw Changed();
            var ajax = new Uri(pageUri, WebUtility.HtmlDecode(endpoint.Groups[1].Value)); CheckApi(ajax);
            var form = Form(script); if (protectedFile) form["p"] = password;
            if (ajax.AbsolutePath == "/ajaxfile.php") form["kd"] = "1";
            if (!form.ContainsKey("action")) form["action"] = "downprocess";
            var data = await Post(ajax, form, pageUri, cancel).ConfigureAwait(false); EnsureSuccess(data);
            string path = Value(data, "url"), domain = Value(data, "dom");
            if (string.IsNullOrEmpty(path) || path == "0" || !Uri.TryCreate(domain, UriKind.Absolute, out var baseUri)) throw Changed();
            var gateway = new Uri(baseUri, "/file/" + path.TrimStart('/')); CheckGateway(gateway);
            string final = await Gateway(gateway, uri, cancel).ConfigureAwait(false);
            string name = protectedFile ? Plain(Value(data, "inf")) : "";
            return new DownloadLink { Fid = id, Name = string.IsNullOrEmpty(name) ? FileName(html, uri) : name, Size = FileSize(html), DownloadUrl = final };
        }
        private async Task<string> Gateway(Uri uri, Uri shareUri, CancellationToken cancel)
        {
            CheckGateway(uri);
            for (int attempt = 0; attempt < 2; attempt++)
            {
            using (var request = Request(uri, null, null))
            {
                AddDownloadCookie(request);
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    RememberCookies(uri, response);
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                    {
                        var final = new Uri(uri, response.Headers.Location); CheckDownload(final); return final.AbsoluteUri;
                    }
                    if (!response.IsSuccessStatusCode) throw HttpError(response);
                    string media = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (media.Length > 0 && !media.Contains("html") && !media.Contains("json") && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return uri.AbsoluteUri;
                    string html = await ReadBody(response, cancel).ConfigureAwait(false);
                    if (CookieChallenge(uri, html)) { if (attempt == 0) continue; throw Verification(); }
                    var form = Form(html); form["el"] = "2";
                    var endpoint = Match(html, @"\burl\s*:\s*['""]([^'""]+)['""]");
                    var ajax = endpoint.Success ? new Uri(uri, endpoint.Groups[1].Value) : new Uri(shareUri, "/ajax.php");
                    var data = await Post(ajax, form, uri, cancel, true).ConfigureAwait(false);
                    if (data.ContainsKey("zt")) EnsureSuccess(data);
                    if (!Uri.TryCreate(Value(data, "url"), UriKind.Absolute, out var finalUri)) throw Changed();
                    CheckDownload(finalUri); return finalUri.AbsoluteUri;
                }
            }
            }
            throw Verification();
        }
        private static PanelApiException Verification() => new PanelApiException("蓝奏云要求浏览器安全验证，请在浏览器打开分享完成验证后重试；当前不自动处理人工验证码。");
        private bool CookieChallenge(Uri uri, string html)
        {
            var arg = Match(html, @"\barg1\s*=\s*['""]([0-9A-F]{40})['""]");
            if (!arg.Success)
            {
                if (Match(html, "acw_sc__v2|arg1\\s*=|滑动验证|安全验证|人机验证").Success) throw Verification();
                return false;
            }
            // Standard acw browser-cookie challenge: a fixed permutation and XOR, no JS evaluation.
            int[] order = { 15,35,29,24,33,16,1,38,10,9,19,31,40,27,22,23,25,13,6,11,39,18,20,8,14,21,32,26,2,30,7,4,17,5,3,28,34,37,12,36 };
            const string mask = "3000176000856006061501533003690027800375";
            var chars = order.Select(i => arg.Groups[1].Value[i - 1]).ToArray();
            var result = new StringBuilder(40);
            for (int i = 0; i < 40; i += 2)
                result.Append((Convert.ToByte(new string(chars, i, 2), 16) ^ Convert.ToByte(mask.Substring(i, 2), 16)).ToString("x2", CultureInfo.InvariantCulture));
            _cookies.Add(uri, new Cookie("acw_sc__v2", result.ToString(), "/"));
            return true;
        }
        private static void AddDownloadCookie(HttpRequestMessage request)
        {
            string existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(existing) ? "down_ip=1" : existing + "; down_ip=1");
        }
        private void RememberCookies(Uri uri, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (string value in values.Take(20))
                if (value.Length <= 4096) { try { _cookies.SetCookies(uri, value); } catch (CookieException) { } }
        }
        private static void CheckPage(Uri uri)
        {
            if (!IsShareHost(uri.Host) || uri.Scheme != "https" && uri.Scheme != "http" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new PanelApiException("蓝奏云返回了不受信任的解析页面地址。");
        }
        private static void CheckGateway(Uri uri)
        {
            CheckDownload(uri);
            if (!IsShareHost(uri.Host) && !uri.Host.EndsWith(".woozooo.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".lanzov.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".lanrar.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".dmpdmp.com", StringComparison.OrdinalIgnoreCase))
                throw new PanelApiException("蓝奏云返回了尚未支持的下载网关域名（" + uri.DnsSafeHost + "），请在浏览器确认链接。");
        }
        private static void CheckApi(Uri uri)
        {
            if (uri.Host.Equals("apifile.woozooo.com", StringComparison.OrdinalIgnoreCase) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/ajaxfile.php") return;
            CheckPage(uri);
        }
        private static void CheckDownload(Uri uri)
        {
            if ((uri.Scheme != "https" && uri.Scheme != "http") || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns || !uri.Host.Contains(".") || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                throw new PanelApiException("蓝奏云返回了无效的下载地址。");
        }
        private HttpRequestMessage Request(Uri uri, Dictionary<string, string> form, Uri referer)
        {
            var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            string cookies = _cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
            if (referer != null) request.Headers.Referrer = referer;
            if (form != null) { request.Content = new FormUrlEncodedContent(form); request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest"); if (referer != null) request.Headers.TryAddWithoutValidation("Origin", referer.GetLeftPart(UriPartial.Authority)); }
            return request;
        }
        private async Task<(string html, Uri uri)> ReadPage(Uri uri, CancellationToken cancel)
        {
            for (int hop = 0; hop < 6; hop++)
            {
                CheckPage(uri);
                using (var request = Request(uri, null, null))
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    RememberCookies(uri, response);
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null) { uri = new Uri(uri, response.Headers.Location); continue; }
                    if (!response.IsSuccessStatusCode) throw HttpError(response);
                    string html = await ReadBody(response, cancel).ConfigureAwait(false);
                    if (Match(html, "文件不存在|取消分享|文件已删除|分享已取消|分享已失效").Success) throw new PanelApiException("蓝奏云分享已失效、被取消或文件不存在。");
                    if (CookieChallenge(uri, html)) { if (hop < 5) continue; throw Verification(); }
                    return (Regex.Replace(html, @"<!--.*?-->|/\*.*?\*/", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1)), uri);
                }
            }
            throw new PanelApiException("蓝奏云分享重定向次数过多。");
        }
        private async Task<Dictionary<string, object>> Post(Uri uri, Dictionary<string, string> form, Uri referer, CancellationToken cancel, bool gateway = false)
        {
            if (gateway) CheckGateway(uri); else CheckApi(uri);
            using (var request = Request(uri, form, referer))
            {
                if (gateway) AddDownloadCookie(request);
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    RememberCookies(uri, response);
                    if (!response.IsSuccessStatusCode) throw HttpError(response);
                    string body = await ReadBody(response, cancel).ConfigureAwait(false);
                    try { return _json.Deserialize<Dictionary<string, object>>(body) ?? throw Changed(); }
                    catch (ArgumentException) { throw Changed(); }
                }
            }
        }
        private static async Task<string> ReadBody(HttpResponseMessage response, CancellationToken cancel)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using (deadline.Token.Register(() => response.Dispose()))
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var bytes = new MemoryStream())
                {
                    byte[] buffer = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                    { if (bytes.Length + read > 2097152) throw new PanelApiException("蓝奏云解析响应过大。"); bytes.Write(buffer, 0, read); }
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }
            }
        }
        private static PanelApiException HttpError(HttpResponseMessage response) => new PanelApiException("蓝奏云请求失败（HTTP " + (int)response.StatusCode + "），请稍后重试或在浏览器检查分享。");
        private static PanelApiException Changed() => new PanelApiException("蓝奏云解析参数缺失或页面规则已变化；请检查提取码，或在浏览器确认分享能否下载。");
        private static void EnsureSuccess(Dictionary<string, object> data)
        {
            string code = Value(data, "zt");
            if (code == "1") return;
            string info = Value(data, "inf");
            if (Match(info, "密码|提取码|password").Success) throw new PanelApiException("蓝奏云提取码错误或缺失，请核对后重试。");
            throw new PanelApiException("蓝奏云拒绝解析（状态 " + (Regex.IsMatch(code, @"^-?\d{1,6}$") ? code : "未知") + "），请检查分享、提取码或浏览器验证要求。");
        }
        private static string Value(Dictionary<string, object> data, string key) => data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        private static bool IsFolder(string html) => Match(html, @"filemoreajax\.php|id\s*=\s*['""]infos['""]").Success;
        private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]*>", "")).Trim();
        private static string FileName(string html, Uri uri)
        {
            foreach (string pattern in new[] { @"<title>(.*?)\s*[-–—]\s*蓝奏[^<]*</title>", @"id\s*=\s*['""]filenajax['""][^>]*>(.*?)</", @"var\s+filename\s*=\s*['""]([^'""]+)['""]", @"class\s*=\s*['""]filethetext['""][^>]*>(.*?)</" })
            { var match = Match(html, pattern); if (match.Success && Plain(match.Groups[1].Value).Length > 0) return Plain(match.Groups[1].Value); }
            return uri.Segments.Last();
        }
        private static long FileSize(string html) => ParseSize(Match(Plain(html), @"大小\W*([0-9.]+\s*[BKMG]B?)").Groups[1].Value);
        private static long ParseSize(string text)
        {
            var match = Match(text, @"^\s*([0-9]+(?:\.[0-9]+)?)\s*([BKMG])B?\s*$");
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return 0;
            double bytes = number * Math.Pow(1024, "BKMG".IndexOf(match.Groups[2].Value.ToUpperInvariant(), StringComparison.Ordinal));
            return bytes >= long.MaxValue ? 0 : (long)bytes;
        }
        // Read literal values and simple variable references only; never execute untrusted JavaScript.
        private static Dictionary<string, string> Form(string html)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match variable in Matches(html, @"\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)\s*=\s*('(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*""|[A-Za-z_$][\w$]*|\d+)\s*;"))
                variables[variable.Groups[1].Value] = variable.Groups[2].Value;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var blocks = Matches(html, @"\bdata\s*:\s*\{([^{}]*)\}");
            var block = blocks.Cast<System.Text.RegularExpressions.Match>().OrderByDescending(m => m.Length).FirstOrDefault();
            if (block == null) throw Changed();
            foreach (System.Text.RegularExpressions.Match field in Matches(block.Groups[1].Value, @"(?:['""](?<key>[\w]+)['""]|(?<key>[\w]+))\s*:\s*(?<value>'(?:\\.|[^'\\])*'|""(?:\\.|[^""\\])*""|[A-Za-z_$][\w$]*|\d+)\s*(?:,|$)"))
            {
                string value = field.Groups["value"].Value;
                for (int i = 0; i < 8 && variables.TryGetValue(value, out var replacement); i++) value = replacement;
                if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"')) value = value.Substring(1, value.Length - 2).Replace("\\/", "/").Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\");
                else if (!Regex.IsMatch(value, @"^\d+$")) continue; // Dynamic password/page fields are supplied by us.
                result[field.Groups["key"].Value] = value;
            }
            if (result.Count == 0) throw Changed();
            return result;
        }
    }
}
