using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;
using ZERA.Core.Models;

namespace ZERA.Core.Panels
{
    public sealed class WebDavAccount
    {
        public string Name { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool AllowHttp { get; set; }
        public string Serialize() { Validate(); return new JavaScriptSerializer().Serialize(this); }
        public static WebDavAccount Parse(string text)
        {
            try { var value = new JavaScriptSerializer().Deserialize<WebDavAccount>(text); value.Validate(); return value; }
            catch { throw new ArgumentException("WebDAV 配置无效，请重新填写服务器地址和账号。"); }
        }
        public void Validate()
        {
            if (!Uri.TryCreate((Endpoint ?? "").Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                uri.Scheme == "http" && !AllowHttp || (Username ?? "").Contains(":") ||
                (Username ?? "").Any(char.IsControl) || (Password ?? "").Any(char.IsControl))
                throw new ArgumentException("请填写不含凭据、查询参数或片段的 HTTP(S) WebDAV 根地址。HTTP 需勾选允许未加密连接，用户名不能包含冒号。");
            Endpoint = uri.AbsoluteUri.TrimEnd('/') + "/";
            Name = string.IsNullOrWhiteSpace(Name) ? uri.Host : Name.Trim();
        }
    }

    public sealed class ScopedAuthorizationHandler : DelegatingHandler
    {
        private readonly Uri _root;
        private readonly string _authorization;
        public ScopedAuthorizationHandler(HttpMessageHandler inner, string scope, string authorization) : base(inner)
        { _root = new Uri(scope); _authorization = authorization; }
        public static bool InScope(Uri root, Uri value)
        {
            if (root.Scheme != value.Scheme || root.Host != value.Host || root.Port != value.Port || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0) return false;
            string prefix = Uri.UnescapeDataString(root.AbsolutePath).TrimEnd('/') + "/";
            string path = Uri.UnescapeDataString(value.AbsolutePath);
            if (System.Text.RegularExpressions.Regex.IsMatch(path, "%2e|%2f|%5c", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
            if (path.Split('/').Any(p => p == "." || p == "..") || path.Contains("\\")) return false;
            return path == prefix.TrimEnd('/') || path.StartsWith(prefix, StringComparison.Ordinal);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (!InScope(_root, request.RequestUri)) throw new HttpRequestException("WebDAV 请求越出配置的服务器根目录，已停止。");
            if (!string.IsNullOrEmpty(_authorization)) request.Headers.TryAddWithoutValidation("Authorization", _authorization);
            return base.SendAsync(request, cancel); // Redirects are disabled in the inner handler.
        }
    }

    public sealed class WebDavClient : IPanelClient
    {
        private static readonly XNamespace Dav = "DAV:";
        private readonly WebDavAccount _account;
        private readonly HttpClient _http;
        private readonly Uri _root;
        private readonly string _credential;
        public PanelKind Kind => PanelKind.WebDav;
        public bool IsLoggedIn => !string.IsNullOrEmpty(_credential);
        public string CookieStr => _credential; // Legacy slot stores the entire encrypted credential document.
        public string AccountKey => PanelAdapter.CredentialKey("dav_", _root.AbsoluteUri + "\n" + _account.Username);
        public WebDavClient(string credential)
        {
            _account = string.IsNullOrEmpty(credential) ? new WebDavAccount { Endpoint = "https://localhost/", Name = "未配置 WebDAV" } : WebDavAccount.Parse(credential);
            _credential = string.IsNullOrEmpty(credential) ? "" : _account.Serialize(); _root = new Uri(_account.Endpoint);
            var inner = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            _http = new HttpClient(new ScopedAuthorizationHandler(inner, _root.AbsoluteUri, Authorization)) { Timeout = TimeSpan.FromSeconds(30) };
        }
        private string Authorization => string.IsNullOrEmpty(_account.Username) && string.IsNullOrEmpty(_account.Password) ? null :
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_account.Username + ":" + _account.Password));
        private Uri Resource(string id)
        {
            var uri = string.IsNullOrEmpty(id) || id == "0" || id == "/" ? _root : new Uri(_root, id);
            if (!ScopedAuthorizationHandler.InScope(_root, uri)) throw new PanelApiException("WebDAV 路径越出配置根目录。");
            return uri;
        }
        private async Task<XDocument> Properties(Uri uri, int depth, CancellationToken cancel)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
            deadline.CancelAfter(TimeSpan.FromSeconds(30)); cancel = deadline.Token;
            using (var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri))
            {
                request.Headers.TryAddWithoutValidation("Depth", depth.ToString());
                request.Content = new StringContent("<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:displayname/><d:getcontentlength/><d:quota-used-bytes/><d:quota-available-bytes/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                using (cancel.Register(() => response.Dispose()))
                {
                    if ((int)response.StatusCode != 207) throw new PanelApiException("WebDAV 查询失败（HTTP " + (int)response.StatusCode + "）。请检查根地址、账号权限及服务器是否支持 PROPFIND；重定向地址请直接填写最终地址。", status: (int)response.StatusCode);
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        var bytes = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false)) > 0)
                        { if (buffer.Length + count > 8 * 1024 * 1024) throw new PanelApiException("WebDAV 目录响应超过 8 MiB，无法完整读取。"); buffer.Write(bytes, 0, count); }
                        buffer.Position = 0;
                        using (var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 }))
                        { var doc = XDocument.Load(reader); if (doc.Root?.Name != Dav + "multistatus") throw new PanelApiException("WebDAV 响应格式无效。"); return doc; }
                    }
                }
            }
            }
        }
        private static XElement SuccessfulProperties(XElement response) => response.Elements(Dav + "propstat")
            .Where(p => ((string)p.Element(Dav + "status") ?? "").Split(' ').Contains("200"))
            .Select(p => p.Element(Dav + "prop")).Where(p => p != null).Aggregate(new XElement(Dav + "prop"), (all, p) => { all.Add(p.Elements()); return all; });
        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            if (!IsLoggedIn) return (false, null);
            var doc = await Properties(_root, 0, cancel).ConfigureAwait(false);
            bool valid = doc.Root.Elements(Dav + "response").Any(r =>
                Uri.TryCreate(_root, (string)r.Element(Dav + "href"), out var uri) && uri.AbsolutePath.TrimEnd('/') == _root.AbsolutePath.TrimEnd('/') &&
                ScopedAuthorizationHandler.InScope(_root, uri) && SuccessfulProperties(r).Descendants(Dav + "collection").Any());
            return (valid, _account.Name);
        }
        public async Task<List<QuarkFileEntry>> ListDirAsync(string directory, int maxPages, CancellationToken cancel = default)
        {
            Uri parent = Resource(directory); var doc = await Properties(parent, 1, cancel).ConfigureAwait(false);
            string parentPath = parent.AbsolutePath.TrimEnd('/') + "/";
            var result = new List<QuarkFileEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var response in doc.Root.Elements(Dav + "response"))
            {
                if (!Uri.TryCreate(parent, (string)response.Element(Dav + "href"), out var uri) || !ScopedAuthorizationHandler.InScope(_root, uri)) continue;
                if (!uri.AbsolutePath.StartsWith(parentPath, StringComparison.Ordinal)) continue;
                string tail = uri.AbsolutePath.Substring(parentPath.Length).TrimEnd('/');
                if (tail.Length == 0 || tail.Contains("/") || !seen.Add(uri.AbsoluteUri)) continue;
                var prop = SuccessfulProperties(response); if (!prop.Elements().Any()) continue;
                string name = Uri.UnescapeDataString(tail);
                if (name == "." || name == ".." || name.Contains("/") || name.Contains("\\") || name.Any(char.IsControl)) continue;
                result.Add(new QuarkFileEntry { Fid = uri.AbsoluteUri, Path = uri.AbsoluteUri, Name = name,
                    IsDir = prop.Descendants(Dav + "collection").Any(), Size = long.TryParse((string)prop.Element(Dav + "getcontentlength"), out long size) ? Math.Max(0, size) : 0 });
            }
            return result;
        }
        public async Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default)
        {
            var doc = await Properties(_root, 0, cancel).ConfigureAwait(false);
            var prop = doc.Root.Elements(Dav + "response").Select(SuccessfulProperties).FirstOrDefault();
            if (!long.TryParse((string)prop?.Element(Dav + "quota-used-bytes"), out long used) || used < 0 ||
                !long.TryParse((string)prop?.Element(Dav + "quota-available-bytes"), out long free) || free < 0 || used > long.MaxValue - free)
                throw new NotSupportedException("服务器未提供有效的 WebDAV 容量信息。");
            return (used + free, used);
        }
        public Task<List<DownloadLink>> ResolveShareAsync(string url, string passcode, int maxItems, CancellationToken cancel = default) => throw new NotSupportedException("WebDAV 使用服务器目录，不解析网盘分享链接。");
        public Task<DownloadLink> ResolveDriveFileAsync(string fileId, CancellationToken cancel = default)
        { cancel.ThrowIfCancellationRequested(); return Task.FromResult(new DownloadLink { Fid = Resource(fileId).AbsoluteUri, DownloadUrl = Resource(fileId).AbsoluteUri }); }
        public Task<string> RefreshLinkAsync(Download.PendingInfo pending, CancellationToken cancel = default)
        { cancel.ThrowIfCancellationRequested(); return Task.FromResult(Resource(pending.Fid).AbsoluteUri); }
        public DownloadRequestContext BuildDownloadContext() => new DownloadRequestContext { Cookie = "", UserAgent = "ZERA-WebDAV/2.8", Referer = "", Origin = "",
            Authorization = Authorization, CredentialScope = _root.AbsoluteUri, SuggestedThreads = 4 };
        public void ShutdownPlatformResources() { }
        public void Dispose() => _http.Dispose();
    }
}
