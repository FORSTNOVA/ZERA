// ZERA —— 多平台网盘下载器
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ZERA.Core.Models;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA.Core.Api
{
    /// <summary>
    /// 百度网盘网页端客户端。
    ///
    /// <para>【与夸克的三个本质差异】这三点决定了本类不能照抄 QuarkClient：</para>
    /// <list type="number">
    /// <item>
    /// <b>登录方式</b>：夸克能靠内置 Python 桥绕过 ALB 的 TLS 指纹限制换 Cookie；
    /// 百度没有这个便利 —— 登录态 BDUSS 只能由用户从浏览器复制粘贴。
    /// 这不是设计偷懒，是百度侧没有可编程的扫码换 Cookie 通道。
    /// </item>
    /// <item>
    /// <b>取链要页面上下文</b>：百度的 <c>sign</c> / <c>timestamp</c> 不在接口返回里，
    /// 必须先 GET 分享页从 HTML 的 <c>yunData</c> 对象里抠出来，再连同
    /// <c>shareid</c> / <c>uk</c> 一起提交给 <c>api/sharedownload</c>。
    /// 夸克则是纯 API 调用，不依赖页面。
    /// </item>
    /// <item>
    /// <b>错误是 errno 而非 HTTP 码</b>：请求返回 HTTP 200 但 JSON 里
    /// <c>errno = -20</c> 表示要验证码。所以**不能只看 HTTP 状态码判断成败**，
    /// 每处调用都要查 errno。
    /// </item>
    /// </list>
    ///
    /// <para>【⚠️ 服务端强制限速】非会员约 100–300 KB/s 且只开放 4 线程。
    /// 这是令牌桶 + 线程双重限制，<b>客户端无法绕过</b>。本类如实
    /// <see cref="BuildDownloadContext"/> 上报该状态，让界面提示用户，
    /// 而不是让用户以为程序有 bug。</para>
    /// </summary>
    public sealed class BaiduClient : IPanelClient
    {
        private readonly QuarkSession _session;
        private bool _vipChecked;
        private bool _isVip;
        public BaiduMembershipInfo Membership { get; private set; }

        // 分享页解析出来的临时上下文，取直链与刷新直链都要用
        private string _shareSign;
        private long _shareTimestamp;
        private string _shareId;
        private string _shareUk;

        /// <summary>
        /// 最近一次解析的分享短码（surl，不含 /s/ 前缀）。
        /// 分享目录浏览（share/list）要用它拼 shorturl 参数。
        /// </summary>
        private string _shareSurl;

        public PanelKind Kind => PanelKind.Baidu;

        /// <summary>
        /// 缓存的 bdstoken。百度所有写接口都要带，它是 CSRF 令牌，
        /// 从 Cookie 旁的页面变量或 gettemplatevariable 取。
        /// </summary>
        private string _bdstoken;

        /// <summary>
        /// 最近一次原生取链响应中的限速参数（字节/秒）。0 = 响应里没带/未知。
        ///
        /// 【为什么要暴露这个】百度非会员的下载速度是服务端按账号下发的
        /// （locatedownload 响应里的 <c>sl</c> 参数，社区逆向证实单位为 KiB/s，
        /// 如 sl=120 → 120×1024 B/s）。这是账号属性，客户端无法绕过 ——
        /// 与其让用户以为"软件下载慢"，不如把服务端的实际配额如实显示出来。
        /// 缺少此参数不能证明不限速，也不能用于判断极速流量是否启用。
        /// </summary>
        public long ServerSpeedLimitBps { get; private set; }

        /// <summary>
        /// 最近一次批量取链的诊断报告（人类可读）。请求了 N 个 fs_id 但百度只
        /// 返回 M 个时，这里记下**缺了哪些、原始 errno/errmsg 是什么**。
        ///
        /// 【为什么需要】sharedownload 对"不能给链"的文件（版权风控、大小超限）
        /// 常见行为是 **errno=0 但该文件不在返回 list 里** —— 从外面看只是
        /// "没有 download_url"，无从判断是风控、限流还是分享被关。
        /// 调用方在"返回数 &lt; 请求数"时读这个属性打日志，把猜测变成事实。
        /// </summary>
        public string LastResolveReport { get; private set; } = "";

        /// <summary>
        /// fs_id → 路径 映射（列目录时从接口返回的 <c>path</c> 字段收集）。
        /// 百度的取链接口按**路径**寻址，而界面/下载链路传的是 fs_id，需要这张表反查。
        /// 只在会话内有效 —— 程序重启后重新浏览一次目录即可重建。
        /// </summary>
        private readonly Dictionary<string, string> _fsPathCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public BaiduClient(string cookie)
        {
            _session = new QuarkSession(cookie, DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PanelSessionProfile.Baidu);
        }

        public bool IsLoggedIn => _session.HasCookie(BaiduConstants.LoginCookieKey);

        /// <summary>把完整 Cookie 串取出来（下载直链时必须原样带上）。</summary>
        public string CookieStr => _session.BuildCookieString();

        // ================= 底层请求 =================

        /// <summary>
        /// 统一请求入口。会：
        ///   1. 经 <see cref="QuarkSession"/> 补全请求头、节流、退避重试；
        ///   2. 解析 JSON；
        ///   3. **检查百度 errno**（这一步是百度特有的，HTTP 200 并不代表成功）。
        /// </summary>
        private async Task<Dictionary<string, object>> RequestJsonAsync(
            HttpMethod method, string url,
            IDictionary<string, string> query = null,
            IDictionary<string, string> form = null,
            int timeoutSeconds = 30,
            CancellationToken cancel = default)
        {
            if (query != null && query.Count > 0)
                url = AppendQuery(url, query);

            // 百度接口经常要求带 bdstoken 作为查询参数
            if (string.IsNullOrEmpty(_bdstoken))
                await EnsureBdstokenAsync(cancel).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(_bdstoken) && url.IndexOf("bdstoken=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                url = AppendQuery(url, new Dictionary<string, string>
                {
                    { "bdstoken", _bdstoken },
                    { "clienttype", BaiduConstants.ClientType },
                    { "app_id", BaiduConstants.AppId },
                    { "web", BaiduConstants.Web },
                });
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using (var resp = await _session.SendWithRetryAsync(() =>
                    {
                        var req = new HttpRequestMessage(method, url);
                        _session.ApplyHeaders(req, url, api: true);
                        if (form != null)
                        {
                            // 百度这几个接口是 application/x-www-form-urlencoded，
                            // 不是 JSON —— 用 JSON 发会返回参数错误。
                            req.Content = new FormUrlEncodedContent(form);
                        }
                        return req;
                    }, 3, cts.Token).ConfigureAwait(false))
                    {
                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        int status = (int)resp.StatusCode;

                        // 【2026-10-15】WAF 拦截与"登录态失效"都会走 403，
                        // 但解法完全相反：前者要放慢/换身份，后者要重新登录。
                        // 区分特征：风控页返回的是 **HTML**，业务错误一定是 JSON。
                        if (TlsWafCompat.IsWafInterception(status, raw))
                            throw new PanelApiException(
                                TlsWafCompat.DescribeWafHint(status), 0, status);

                        if (status == 401)
                            throw new PanelApiException(
                                "访问被拒绝(401)，登录态可能已失效，请重新导入 Cookie", 0, status);
                        if (status == 429)
                            throw new PanelApiException("请求过于频繁(429)，请稍后再试", 0, 429);

                        object parsed;
                        try
                        {
                            parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                                .DeserializeObject(raw);
                        }
                        catch (Exception ex)
                        {
                            throw new PanelApiException(
                                "接口返回非 JSON（HTTP " + (int)resp.StatusCode + "): "
                                + Truncate(raw, 200), ex);
                        }

                        var dict = parsed as Dictionary<string, object>;
                        if (dict == null)
                            throw new PanelApiException("接口返回结构异常");

                        // ★ 百度特有：HTTP 200 也要看 errno
                        ThrowIfErrno(dict);
                        return dict;
                    }
                }
                catch (PanelApiException) { throw; }
                // 【2026-10-15】TLS 层失败原样上抛：它不是"接口报错"，
                // 重试无意义（详见 TlsHandshakeException 的说明）。
                catch (TlsHandshakeException) { throw; }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;   // 用户主动取消，原样上抛
                }
                catch (OperationCanceledException)
                {
                    throw new PanelApiException("请求超时(" + timeoutSeconds + "s): " + url);
                }
                catch (Exception ex)
                {
                    throw new PanelApiException("网络请求失败: " + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// 检查百度的 <c>errno</c>，非 0 则抛带语义的异常。
        ///
        /// 【关键】errno = -20（要验证码）时**必须让异常带 <c>NeedsCaptcha = true</c>**，
        /// 上层据此停止自动重试并提示用户手工过码 —— 自动重试只会延长风控冷却。
        /// </summary>
        private static void ThrowIfErrno(Dictionary<string, object> res)
        {
            object rawErrno = Get(res, "errno");
            if (rawErrno == null) return;   // 有些接口（如 gettemplatevariable）不返回 errno

            int errno = (int)AsLong(rawErrno);
            if (errno == BaiduConstants.ErrOk) return;

            string msg = AsString(Get(res, "error_msg")) ?? AsString(Get(res, "show_msg"));
            string text = ExplainErrno(errno, msg);

            switch (errno)
            {
                case BaiduConstants.ErrNeedCaptcha:
                    throw new PanelApiException(text, errno, 0, needsCaptcha: true);
                case BaiduConstants.ErrNotLogin:
                    throw new PanelApiException(text, errno);
                default:
                    throw new PanelApiException(text, errno);
            }
        }

        /// <summary>把百度 errno 翻译成可操作的中文提示。</summary>
        public static string ExplainErrno(int errno, string serverMsg)
        {
            string baseText;
            switch (errno)
            {
                case BaiduConstants.ErrNeedCaptcha:
                    baseText = "百度要求图形验证码（请求过于频繁）。请降低使用频率或更换网络后重试";
                    break;
                case BaiduConstants.ErrNotLogin:
                    baseText = "登录态已失效，请到登录页重新扫码（或重新导入 Cookie）";
                    break;
                case BaiduConstants.ErrBadPasscode:
                    baseText = "提取码错误，请检查后重试";
                    break;
                case BaiduConstants.ErrViolation:
                    baseText = "该分享内容因违规被限制，无法下载";
                    break;
                case BaiduConstants.ErrShareExpired:
                    baseText = "分享已过期（113），请向分享者索取新链接";
                    break;
                case BaiduConstants.ErrShareNotFound:
                    baseText = "分享不存在或已被取消（116）";
                    break;
                case BaiduConstants.ErrNoPermission:
                    baseText = "没有下载权限（118），分享者可能禁止了下载";
                    break;
                case BaiduConstants.ErrTooManyFiles:
                    baseText = "一次操作的文件过多（121），请分批选择";
                    break;
                case BaiduConstants.ErrPackageTooLarge:
                    baseText = "文件夹打包超过百度 300MB 上限（31090）。"
                             + "本程序已按单文件逐个下载，若仍出现请检查是否有超大单文件";
                    break;
                case BaiduConstants.ErrDownloadFail:
                    baseText = "下载失败（2），请稍后重试";
                    break;
                default:
                    baseText = "百度接口返回错误（errno " + errno + "）";
                    break;
            }

            if (!string.IsNullOrEmpty(serverMsg) && serverMsg.Length > 0)
                baseText += "：服务端信息：" + Truncate(serverMsg, 120);
            return baseText;
        }

        /// <summary>取 bdstoken（CSRF 令牌）。取不到不影响只读接口，故失败不抛异常。</summary>
        private async Task EnsureBdstokenAsync(CancellationToken cancel)
        {
            if (!string.IsNullOrEmpty(_bdstoken)) return;
            try
            {
                var res = await RequestJsonNoErrnoAsync(HttpMethod.Get,
                    BaiduConstants.TokenApi, cancel: cancel).ConfigureAwait(false);
                _bdstoken = AsString(Get(res, "result", "bdstoken"));
            }
            catch
            {
                // 没有 bdstoken 时，只有写接口会失败；读接口仍可用。
                // 这里刻意吞掉异常 —— 不因为取不到令牌就判定整个客户端不可用。
            }
        }

        /// <summary>
        /// 发一个 form-urlencoded 的 POST，**只解析不判 errno**，返回原始字典。
        ///
        /// 用于 <c>/share/verify</c>：它失败时的 errno 本身就是我们要的信息
        /// （-9 提取码错 / -20 要验证码），不能在这里抛异常把上下文吃掉，
        /// 得交给调用方按 errno 分支处理。
        /// </summary>
        private async Task<Dictionary<string, object>> RequestFormAsync(
            string url, IDictionary<string, string> form, CancellationToken cancel)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                using (var resp = await _session.SendWithRetryAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    _session.ApplyHeaders(req, url, api: true);
                    req.Content = new FormUrlEncodedContent(form);
                    return req;
                }, 2, cts.Token).ConfigureAwait(false))
                {
                    string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    try
                    {
                        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                            .DeserializeObject(raw) as Dictionary<string, object>;
                    }
                    catch (Exception ex)
                    {
                        throw new PanelApiException("接口返回非 JSON: " + Truncate(raw, 200), ex);
                    }
                }
            }
        }

        /// <summary>
        /// 请求但不检查 errno（用于那些 errno 无意义、且失败也不致命的接口，
        /// 比如取 bdstoken 本身）。
        /// </summary>
        private async Task<Dictionary<string, object>> RequestJsonNoErrnoAsync(
            HttpMethod method, string url,
            IDictionary<string, string> query = null,
            CancellationToken cancel = default)
        {
            if (query != null && query.Count > 0)
                url = AppendQuery(url, query);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using (var resp = await _session.SendWithRetryAsync(() =>
                {
                    var req = new HttpRequestMessage(method, url);
                    _session.ApplyHeaders(req, url, api: true);
                    return req;
                }, 2, cts.Token).ConfigureAwait(false))
                {
                    string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .DeserializeObject(raw) as Dictionary<string, object>;
                }
            }
        }

        // ================= 登录态 =================

        public async Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default)
        {
            if (!IsLoggedIn)
                return (false, null);

            // 未登录时百度会返回 HTML 登录页而非 JSON，所以先查一个轻量的 JSON 接口
            var res = await RequestJsonNoErrnoAsync(HttpMethod.Get,
                BaiduConstants.CheckLoginApi, cancel: cancel).ConfigureAwait(false);
            if (res == null)
                return (false, null);

            var result = AsDict(Get(res, "result"));
            string username = result != null ? AsString(Get(result, "username")) : null;
            if (string.IsNullOrEmpty(username))
                return (false, null);

            // 顺带探测会员状态：影响线程数建议。失败不影响登录判定。
            await ProbeVipAsync(cancel).ConfigureAwait(false);
            return (true, username);
        }

        /// <summary>
        /// 探测是否会员。用于决定建议线程数 —— 非会员被限速到 4 线程，
        /// 开多了既无收益又会触发风控。
        /// </summary>
        private async Task ProbeVipAsync(CancellationToken cancel)
        {
            if (_vipChecked) return;
            try
            {
                await QueryMembershipAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch
            {
                // A failed request is unknown; keep the last valid state and allow a later retry.
            }
        }

        public async Task<BaiduMembershipInfo> QueryMembershipAsync(CancellationToken cancel = default)
        {
            if (!IsLoggedIn) throw new InvalidOperationException("请先登录百度网盘账号。");
            var res = await RequestJsonNoErrnoAsync(HttpMethod.Get, BaiduConstants.MembershipApi, cancel: cancel).ConfigureAwait(false);
            var info = BaiduMembershipInfo.Parse(res);
            Membership = info;
            _isVip = info.IsMember == true;
            _vipChecked = info.IsMember.HasValue;
            return info;
        }

        public async Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default)
        {
            var res = await RequestJsonAsync(HttpMethod.Get, BaiduConstants.QuotaApi,
                cancel: cancel).ConfigureAwait(false);
            long total = AsLong(Get(res, "total"));
            long used = AsLong(Get(res, "used"));
            return (total, used);
        }

        // ================= 我的网盘 =================

        /// <summary>
        /// 列出「我的网盘」目录内容。
        ///
        /// 【2026-10-14 晚修 · 两个致命 bug】
        /// ① 原来把夸克的根 fid <c>"0"</c> 原样当成百度的 <c>dir</c> 参数发出去
        ///    （<c>/api/list?dir=0</c>），而百度要的是**路径**（根目录是 <c>"/"</c>）
        ///    → 登录后列表恒为空，用户反馈"看不到网盘里存的文件"；
        /// ② 双击进入子目录时传的是 fs_id，百度同样不认 → 二级目录也进不去。
        /// 现在统一归一化为路径寻址；fs_id 经 <see cref="_fsPathCache"/> 转换。
        /// </summary>
        public async Task<List<QuarkFileEntry>> ListDirAsync(string dirFidOrPath,
            int maxPages, CancellationToken cancel = default)
        {
            string dir = NormalizeDir(dirFidOrPath);

            var merged = new List<QuarkFileEntry>();
            for (int page = 1; page <= Math.Max(1, maxPages); page++)
            {
                var query = new Dictionary<string, string>
                {
                    { "dir", dir },
                    { "order", "name" },
                    { "desc", "0" },
                    { "start", ((page - 1) * 100).ToString(CultureInfo.InvariantCulture) },
                    { "limit", "100" },
                    { "web", BaiduConstants.Web },
                };

                var res = await RequestJsonAsync(HttpMethod.Get, BaiduConstants.FileList,
                    query, cancel: cancel).ConfigureAwait(false);

                var list = AsArray(Get(res, "list")) ?? new object[0];
                if (list.Length == 0) break;

                foreach (var o in list)
                {
                    var it = AsDict(o);
                    if (it == null) continue;
                    string fsId = AsString(Get(it, "fs_id")) ?? "";
                    string path = AsString(Get(it, "path"));
                    // 收下 path：取链时要用 fs_id 反查路径
                    if (!string.IsNullOrEmpty(fsId) && !string.IsNullOrEmpty(path))
                        _fsPathCache[fsId] = path;
                    merged.Add(new QuarkFileEntry
                    {
                        // 百度叫 fs_id，映射到统一的 Fid
                        Fid = fsId,
                        Name = AsString(Get(it, "server_filename")) ?? "",
                        Size = AsLong(Get(it, "size")),
                        IsDir = AsLong(Get(it, "isdir")) == 1,
                        Token = "",   // 分享上下文才需要 token
                        Path = path,
                    });
                }
                if (list.Length < 100) break;   // 最后一页
            }
            return merged;
        }

        /// <summary>
        /// 列出**分享**目录内容（<c>/share/list</c>）。
        ///
        /// 【2026-10-14 晚修】此前 UI 列分享目录用的是 <see cref="ListDirAsync"/>
        /// （网盘接口 <c>/api/list</c>）—— 解析分享后看到的是用户**自己网盘的根目录**
        /// （修复 dir 参数后）或空列表（修复前），内容完全是错的，属于最坏的
        /// "静默展示错误内容"一类问题。分享浏览是另一套接口：按
        /// <c>shorturl</c> + <c>sekey</c>（/share/verify 种下的 BDCLND Cookie）寻址，
        /// <c>dir</c> 传分享内路径（根目录不带）。
        /// </summary>
        public async Task<List<QuarkFileEntry>> ListShareDirAsync(string dir,
            int maxPages, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(_shareSurl))
                throw new PanelApiException("还没有加载分享上下文，请先解析分享链接");

            // sekey = BDCLND Cookie 的值（有提取码的分享必须带；无码分享不带也行）
            string sekey = null;
            try
            {
                var jar = _session.Jar.GetCookies(new Uri(BaiduConstants.ApiBase));
                var ck = jar["BDCLND"];
                if (ck != null) sekey = ck.Value;
            }
            catch
            {
                // 取不到就不带 —— 无码分享不需要 sekey
            }

            var merged = new List<QuarkFileEntry>();
            int pages = Math.Max(1, maxPages);
            for (int page = 1; page <= pages; page++)
            {
                var query = new Dictionary<string, string>
                {
                    { "shorturl", _shareSurl },
                    { "root", "1" },
                    { "page", page.ToString(CultureInfo.InvariantCulture) },
                    { "num", "100" },
                    { "web", BaiduConstants.Web },
                };
                // 分享根目录不带 dir（"0"/空都按根目录处理）；子目录传分享内路径
                if (!string.IsNullOrEmpty(dir) && dir != "0")
                    query["dir"] = dir;
                if (!string.IsNullOrEmpty(sekey))
                    query["sekey"] = sekey;

                var res = await RequestJsonAsync(HttpMethod.Get, BaiduConstants.ShareList,
                    query, cancel: cancel).ConfigureAwait(false);

                var list = AsArray(Get(res, "list")) ?? new object[0];
                if (list.Length == 0) break;

                foreach (var o in list)
                {
                    var it = AsDict(o);
                    if (it == null) continue;
                    merged.Add(new QuarkFileEntry
                    {
                        // 分享里同样叫 fs_id，映射到统一的 Fid
                        Fid = AsString(Get(it, "fs_id")) ?? "",
                        Name = AsString(Get(it, "server_filename")) ?? "",
                        Size = AsLong(Get(it, "size")),
                        IsDir = AsLong(Get(it, "isdir")) == 1,
                        Token = "",   // 分享取链的 sign/timestamp 在客户端内部持有
                        Path = AsString(Get(it, "path")),
                    });
                }
                if (list.Length < 100) break;   // 最后一页
            }
            return merged;
        }

        /// <summary>
        /// 把界面传来的"目录键"归一化成百度要的**路径**。
        /// 夸克根 fid <c>"0"</c>/空 → <c>"/"</c>；已是路径 → 原样；
        /// 裸 fs_id → 查 <see cref="_fsPathCache"/> 转路径（查不到给可操作的报错，
        /// 而不是发一个必然失败的请求）。
        /// </summary>
        private string NormalizeDir(string dirFidOrPath)
        {
            if (string.IsNullOrEmpty(dirFidOrPath) || dirFidOrPath == "0")
                return "/";
            if (dirFidOrPath.StartsWith("/", StringComparison.Ordinal))
                return dirFidOrPath;

            string path;
            if (_fsPathCache.TryGetValue(dirFidOrPath, out path))
                return path;

            throw new PanelApiException(
                "百度网盘按路径列目录，无法直接使用文件 ID（" + dirFidOrPath + "）。"
                + "请从根目录重新进入该文件夹（程序重启后需要重新浏览一次）");
        }

        /// <summary>
        /// 取「我的网盘」文件的下载直链。
        ///
        /// 【2026-10-14 晚重做】原来走分享接口 <c>/api/sharedownload</c>（带 primaryid）——
        /// 那是**分享**语义，用到自己网盘的文件上属于碰运气。现按"加速下载用原生版本"
        /// 的要求改为三级路径，全部按**路径**寻址（路径从 <see cref="_fsPathCache"/> 反查）：
        ///   ① <b>原生客户端协议栈</b>：POST <c>pcs.baidu.com/rest/2.0/pcs/file?method=locatedownload</c>，
        ///      netdisk UA + app_id 266719（BaiduPCS-Go 沿用多年的官方客户端身份）——
        ///      服务端按 UA 分带宽池，这是非会员侧唯一可用的加速杠杆；
        ///   ② 网页端 xpan 下载接口（浏览器 UA + app_id 250528）；
        ///   ③ 保底：原来的 sharedownload 路径（不删旧路，防前两者同时失效）。
        /// </summary>
        public async Task<DownloadLink> ResolveDriveFileAsync(string fileId,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(fileId))
                throw new PanelApiException("缺少文件 ID");

            string path;
            if (!_fsPathCache.TryGetValue(fileId, out path))
            {
                // 路径缓存里没有 → 这个文件多半来自**分享**（分享解析只收 fs_id，不收 path）。
                // 分享上下文还在（本次进程解析过该分享）→ 直接走分享取链分支；
                // 否则（自己的网盘文件但本会话没浏览过它的目录）→ 给可操作的报错。
                //
                // 【2026-10-14 晚修】此前这里无条件抛"缺少路径信息"，而 UI 的
                // GetBaiduLinksAsync / RefreshLinkAsync 对**所有**百度文件（含分享文件）
                // 都调用本方法 —— 分享下载的取链与刷新会被这个错误打断。
                if (!string.IsNullOrEmpty(_shareSign) && !string.IsNullOrEmpty(_shareId))
                {
                    var shareLinks = await ResolveBatchAsync(new List<string> { fileId }, cancel)
                        .ConfigureAwait(false);
                    var shareLink = shareLinks.FirstOrDefault(l => !string.IsNullOrEmpty(l.DownloadUrl));
                    if (shareLink != null)
                        return shareLink;
                    throw new PanelApiException(
                        "分享取链失败：该文件可能已被分享者取消，或超出游客可获取的大小限制");
                }
                // Restarted drive tasks persist fs_id, but this in-memory path cache
                // is empty. Resolve exactly that ID instead of requiring UI browsing.
                if (Regex.IsMatch(fileId, @"^[0-9]+$"))
                {
                    var metadata = await RequestJsonAsync(HttpMethod.Get, BaiduConstants.ApiBase + "/api/filemetas",
                        new Dictionary<string, string> { { "fsids", "[" + fileId + "]" }, { "dlink", "0" }, { "detail", "1" } },
                        cancel: cancel).ConfigureAwait(false);
                    var entries = AsArray(Get(metadata, "info"));
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                        {
                            var info = AsDict(entry);
                            if (AsString(Get(info, "fs_id")) != fileId) continue;
                            path = AsString(Get(info, "path"));
                            if (!string.IsNullOrEmpty(path) && path.StartsWith("/", StringComparison.Ordinal))
                            {
                                _fsPathCache[fileId] = path;
                                break;
                            }
                            path = null;
                        }
                    }
                }
                if (string.IsNullOrEmpty(path))
                    throw new PanelApiException("无法恢复该文件的网盘路径，请确认文件仍存在，或进入所在目录后重试");
            }

            string lastErr = null;

            // ① 原生客户端协议栈（优先）—— 进客户端带宽池
            try
            {
                var link = await ResolveViaNativeAsync(path, cancel).ConfigureAwait(false);
                if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                    return WithFid(link, fileId);
            }
            catch (PanelApiException ex)
            {
                lastErr = "原生取链失败: " + ex.Message;
            }

            // ② 网页端 xpan 下载接口
            try
            {
                var link = await ResolveViaXpanAsync(path, cancel).ConfigureAwait(false);
                if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                    return WithFid(link, fileId);
            }
            catch (PanelApiException ex)
            {
                lastErr = (lastErr == null ? null : lastErr + "；") + "网页端取链失败: " + ex.Message;
            }

            // ③【2026-10-15 新增】网页端「下载自己网盘文件」的标准接口 /api/download。
            // ①② 都可能对某些文件返回"errno=0 但没有直链"（百度对损坏/被屏蔽的分卷
            // 文件就是如此），而 /api/download（product=pc）是网页版下载按钮真正走的那条路，
            // 对同样这批文件往往仍能拿到链。必须有 fs_id（不是 path）—— 故传 fileId。
            try
            {
                var link = await ResolveViaWebDownloadAsync(fileId, cancel).ConfigureAwait(false);
                if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                    return WithFid(link, fileId);
            }
            catch (PanelApiException ex)
            {
                lastErr = (lastErr == null ? null : lastErr + "；") + "网页下载接口失败: " + ex.Message;
            }

            // ④ 保底：分享接口（语义不完全对口，但留着防万一）。
            // ⚠️ 必须 POST —— 原实现写成 HttpMethod.Get + form，.NET 会直接抛
            // "无法发送具有此谓词类型的内容正文"（HttpClient 禁止 GET 带请求体），
            // 这条兜底路径自引入起就从未真正跑通过。
            try
            {
                var res = await RequestJsonAsync(HttpMethod.Post, BaiduConstants.Sharedownload,
                    new Dictionary<string, string>
                    {
                        { "encrypt", "0" },
                        { "product", "share" },
                        { "primaryid", _shareId ?? "" },
                        { "fid_list", "[" + fileId + "]" },
                    },
                    form: new Dictionary<string, string>
                    {
                        { "type", "nolimit" },
                        { "channel", BaiduConstants.Channel },
                    },
                    cancel: cancel).ConfigureAwait(false);

                var link = FirstLinkFromResponse(res, fileId);
                if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                    return link;
                lastErr = (lastErr == null ? null : lastErr + "；") + "分享接口未返回直链";
            }
            catch (PanelApiException ex)
            {
                lastErr = (lastErr == null ? null : lastErr + "；") + "分享接口取链失败: " + ex.Message;
            }

            throw new PanelApiException("百度取链失败（已尝试原生/网页端xpan/网页下载/分享四条路径）"
                + (string.IsNullOrEmpty(lastErr) ? "" : "：" + Truncate(lastErr, 300)));
        }

        /// <summary>
        /// 网页端标准下载接口：<c>POST /api/download</c>，form 带
        /// <c>product=pc</c> + <c>fid_list</c> + <c>type=nolimit</c>，bdstoken 由
        /// <see cref="RequestJsonAsync"/> 自动补。这是网页版「下载」按钮的实际请求。
        /// </summary>
        private async Task<DownloadLink> ResolveViaWebDownloadAsync(string fileId,
            CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(fileId))
                throw new PanelApiException("缺少文件 ID");

            await EnsureBdstokenAsync(cancel).ConfigureAwait(false);

            var res = await RequestJsonAsync(HttpMethod.Post, BaiduConstants.WebDownload,
                new Dictionary<string, string>
                {
                    { "channel", BaiduConstants.Channel },
                    { "web", BaiduConstants.Web },
                    { "app_id", BaiduConstants.AppId },
                    { "clienttype", BaiduConstants.ClientType },
                },
                form: new Dictionary<string, string>
                {
                    { "encrypt", "0" },
                    { "product", "pc" },
                    { "fid_list", "[" + fileId + "]" },
                    { "type", "nolimit" },
                },
                cancel: cancel).ConfigureAwait(false);

            // RequestJsonAsync 已查过 errno（≠0 会抛出带语义的异常），到这里是 errno=0
            var link = FirstLinkFromResponse(res, fileId);
            if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                return link;

            long sl = ParseSpeedLimit(res);
            ServerSpeedLimitBps = sl;
            throw new PanelApiException(
                "接口未返回该文件的 dlink（errno=0，响应字段: " + DescribeResponseKeys(res) + "）");
        }

        /// <summary>
        /// 把响应顶层字段名列出来（诊断用）。
        /// 百度对"不给链"的常见回应是 errno=0 + 空列表，只说"没有直链"等于没说；
        /// 把服务端实际给了哪些字段打出来，才能判断是接口形态变了还是文件本身被拒。
        /// </summary>
        private static string DescribeResponseKeys(Dictionary<string, object> res)
        {
            if (res == null) return "(空)";
            var keys = res.Keys.ToList();
            return keys.Count == 0 ? "(空对象)" : string.Join(",", keys.ToArray());
        }

        private static DownloadLink WithFid(DownloadLink link, string fid)
        {
            link.Fid = fid;
            return link;
        }

        /// <summary>
        /// 原生客户端协议栈取链：<c>POST pcs.baidu.com/rest/2.0/pcs/file?method=locatedownload</c>。
        /// 用 netdisk UA + app_id 266719 —— 官方客户端取自己网盘文件的方式。
        /// 响应形态：<c>{"urls":[{"url":"https://d.pcs.baidu.com/…"}], …}</c>（部分版本用 dlink）。
        /// </summary>
        private async Task<DownloadLink> ResolveViaNativeAsync(string path,
            CancellationToken cancel)
        {
            string url = BaiduConstants.PcsFileApi
                + "?method=locatedownload&app_id=" + BaiduConstants.NativeAppId;

            var res = await RequestWithUaAsync(HttpMethod.Post, url,
                new Dictionary<string, string> { { "path", path } },
                BaiduConstants.DownloadUa, cancel).ConfigureAwait(false);

            if (res == null)
                throw new PanelApiException("原生取链接口返回为空");

            ThrowIfErrno(res);

            // 服务端下发的账号限速值（sl，单位 KiB/s）——如实记录，0 = 没带
            long sl = ParseSpeedLimit(res);
            if (sl > 0) ServerSpeedLimitBps = sl;

            var urls = AsArray(Get(res, "urls"));
            if (urls != null)
            {
                foreach (var o in urls)
                {
                    var it = AsDict(o);
                    if (it == null) continue;
                    string u = AsString(Get(it, "url"));
                    if (!string.IsNullOrEmpty(u))
                        return new DownloadLink { DownloadUrl = u, SpeedLimitBps = sl };
                }
            }
            string dlink = AsString(Get(res, "dlink"));
            if (!string.IsNullOrEmpty(dlink))
                return new DownloadLink { DownloadUrl = dlink, SpeedLimitBps = sl };

            // 【2026-10-15】原来这里 return null —— 上层看到的是"没有 download_url"，
            // 而"errno=0 却没发链"恰恰是最需要解释的一种（文件被屏蔽/损坏分卷）。
            // 改成抛异常，让失败原因进入上层日志的 lastErr。
            throw new PanelApiException(
                "原生取链未返回直链（errno=" + AsLong(Get(res, "errno"))
                + "，响应字段: " + DescribeResponseKeys(res) + "）");
        }

        /// <summary>
        /// 从取链响应里解析服务端下发的账号限速值（字节/秒）。0 = 没带（不臆测）。
        /// 依据：locatedownload 响应携带 <c>sl</c> 参数（社区逆向：单位 KiB/s）。
        /// 只认这个确定的字段 —— 宁可显示"未知"，也不要把别的字段硬当成限速值。
        /// </summary>
        private static long ParseSpeedLimit(Dictionary<string, object> res)
        {
            if (res == null) return 0;
            long kib = AsLong(Get(res, "sl"));
            if (kib <= 0) return 0;
            return kib * 1024;
        }

        /// <summary>
        /// 网页端取链：<c>POST /rest/2.0/xpan/file?method=download</c>（浏览器 UA + app_id 250528）。
        /// 这是当前网页版自己调的下自己的文件接口，作为原生路径失败时的回退。
        /// </summary>
        private async Task<DownloadLink> ResolveViaXpanAsync(string path,
            CancellationToken cancel)
        {
            string url = BaiduConstants.XpanFileApi
                + "?method=download&app_id=" + BaiduConstants.AppId
                + "&web=" + BaiduConstants.Web;

            var res = await RequestJsonAsync(HttpMethod.Post, url,
                form: new Dictionary<string, string> { { "path", path } },
                cancel: cancel).ConfigureAwait(false);

            string dlink = AsString(Get(res, "dlink"));
            if (!string.IsNullOrEmpty(dlink))
                return new DownloadLink { DownloadUrl = dlink };

            var urls = AsArray(Get(res, "urls"));
            if (urls != null)
            {
                foreach (var o in urls)
                {
                    var it = AsDict(o);
                    if (it == null) continue;
                    string u = AsString(Get(it, "url"));
                    if (!string.IsNullOrEmpty(u))
                        return new DownloadLink { DownloadUrl = u };
                }
            }
            // 【2026-10-15】同原生路径：静默返回 null 等于把"服务端给了什么"全丢掉
            throw new PanelApiException(
                "网页端 xpan 未返回直链（errno=" + AsLong(Get(res, "errno"))
                + "，响应字段: " + DescribeResponseKeys(res) + "）");
        }

        /// <summary>
        /// 带自定义 UA 的 form 请求（原生客户端协议栈专用）。
        /// 与 <see cref="RequestFormAsync"/> 的两个区别：① 不自动补 bdstoken
        /// （PCS 接口不用网页端那套 CSRF）；② UA 可覆盖成 netdisk 客户端身份。
        /// </summary>
        private async Task<Dictionary<string, object>> RequestWithUaAsync(
            HttpMethod method, string url, IDictionary<string, string> form,
            string userAgent, CancellationToken cancel)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                using (var resp = await _session.SendWithRetryAsync(() =>
                {
                    var req = new HttpRequestMessage(method, url);
                    _session.ApplyHeaders(req, url, api: true);
                    if (!string.IsNullOrEmpty(userAgent))
                    {
                        // ApplyHeaders 刚按画像设过浏览器 UA，这里换成客户端身份
                        req.Headers.Remove("User-Agent");
                        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    }
                    if (form != null)
                        req.Content = new FormUrlEncodedContent(form);
                    return req;
                }, 2, cts.Token).ConfigureAwait(false))
                {
                    string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                        throw new PanelApiException(
                            "访问被拒绝(" + (int)resp.StatusCode + ")，登录态可能已失效，请重新登录",
                            0, (int)resp.StatusCode);
                    try
                    {
                        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                            .DeserializeObject(raw) as Dictionary<string, object>;
                    }
                    catch (Exception ex)
                    {
                        throw new PanelApiException("接口返回非 JSON: " + Truncate(raw, 200), ex);
                    }
                }
            }
        }

        // ================= 分享链接 =================

        /// <summary>
        /// 解析分享链接并取出其中所有**单文件**的直链。
        ///
        /// 【为什么不支持整个文件夹打包成一个 zip】百度对打包下载有 300MB 硬上限
        /// （超了报 31090）。本方法因此只返回文件节点，遇到目录就递归展开，
        /// 调用方拿到的是一串可以逐个下载的直链 —— 这是唯一能可靠处理大文件夹的方式。
        /// </summary>
        public async Task<List<DownloadLink>> ResolveShareAsync(string urlOrCode,
            string passcode, int maxItems, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(urlOrCode))
                throw new PanelApiException("链接为空");

            string surl = ExtractSurl(urlOrCode);
            if (string.IsNullOrEmpty(surl))
                throw new PanelApiException("不是有效的百度网盘分享链接");

            int cap = maxItems > 0 ? Math.Min(maxItems, BaiduConstants.MaxResolveItems)
                                   : BaiduConstants.MaxResolveItems;

            // 1. GET 分享页 → 从 HTML 抠 sign/timestamp/shareid/uk
            await LoadSharePageAsync(surl, passcode, cancel).ConfigureAwait(false);

            // 2. 列目录 → 递归展开所有文件
            var fsIds = new List<string>();
            await CollectFileIdsAsync(surl, string.IsNullOrEmpty(passcode) ? "" : passcode,
                "", fsIds, cap, cancel).ConfigureAwait(false);

            if (fsIds.Count == 0)
                throw new PanelApiException("分享里没有找到可下载的文件");

            // 3. 批量取直链。百度 sharedownload 支持一次传多个 fs_id，
            //    但数量过多会返回 121（操作文件过多），所以分批。
            var links = new List<DownloadLink>();
            const int batch = 50;
            for (int i = 0; i < fsIds.Count && links.Count < cap; i += batch)
            {
                var take = Math.Min(batch, fsIds.Count - i);
                var batchIds = fsIds.GetRange(i, take);
                try
                {
                    links.AddRange(await ResolveBatchAsync(batchIds, cancel).ConfigureAwait(false));
                }
                catch (PanelApiException ex) when (ex.Code == BaiduConstants.ErrTooManyFiles)
                {
                    // 整批被拒（121 = 操作文件过多）。退化为逐个取链 ——
                    // 单个文件永远不会触发"文件过多"。
                    foreach (var one in batchIds)
                    {
                        try
                        {
                            // ⚠️ ResolveBatchAsync 返回的是 List，必须 AddRange ——
                            //    写成 Add(await ...) 会把整个 List 当成一个元素塞进去，
                            //    编译能过（Add(T) 的 T 恰好是 DownloadLink？不，过不了编译），
                            //    但若误写成 links.Add(list[0]) 就会静默丢掉其余 49 个文件。
                            links.AddRange(await ResolveBatchAsync(new List<string> { one }, cancel)
                                .ConfigureAwait(false));
                        }
                        catch { /* 单个失败不中断整批 */ }
                    }
                }
            }

            if (links.Count > cap) links.RemoveRange(cap, links.Count - cap);
            return links;
        }

        /// <summary>批量取直链（内部按 50 个一批拆）。</summary>
        private async Task<List<DownloadLink>> ResolveBatchAsync(List<string> fsIds,
            CancellationToken cancel)
        {
            if (fsIds == null || fsIds.Count == 0) return new List<DownloadLink>();

            var fidList = "[" + string.Join(",", fsIds.ConvertAll(s => "\"" + s + "\"")) + "]";

            var res = await RequestJsonAsync(HttpMethod.Post, BaiduConstants.Sharedownload,
                new Dictionary<string, string>
                {
                    { "sign", _shareSign ?? "" },
                    { "timestamp", _shareTimestamp.ToString(CultureInfo.InvariantCulture) },
                    { "channel", BaiduConstants.Channel },
                    { "web", BaiduConstants.Web },
                    { "app_id", BaiduConstants.AppId },
                    { "clienttype", BaiduConstants.ClientType },
                    { "primaryid", _shareId ?? "" },
                    { "uk", _shareUk ?? "" },
                },
                form: new Dictionary<string, string>
                {
                    { "encrypt", "0" },
                    { "product", "share" },
                    { "fid_list", fidList },
                    { "extra", "{\"isnotify\":0}" },
                },
                cancel: cancel).ConfigureAwait(false);

            var list = AsArray(Get(res, "list")) ?? new object[0];
            var outLinks = new List<DownloadLink>();
            foreach (var o in list)
            {
                var link = ParseDlinkEntry(o);
                if (link != null) outLinks.Add(link);
            }

            // ---- 诊断：errno=0 但文件缺失时，把"缺了谁、服务端说了什么"记下来 ----
            // 百度对风控/超限文件的典型行为就是静默不从 list 里返回，
            // 不记录的话上层只能看到"无 download_url"，永远定位不到原因。
            LastResolveReport = BuildResolveReport(fsIds, outLinks, res);
            return outLinks;
        }

        /// <summary>
        /// 生成批量取链诊断报告：请求 vs 返回的差集 + 原始 errno/errmsg。
        /// 全部取到时返回空串（不打扰正常路径的日志）。
        /// </summary>
        private static string BuildResolveReport(List<string> requested,
            List<DownloadLink> got, Dictionary<string, object> res)
        {
            var gotFids = new HashSet<string>(
                got.Where(l => !string.IsNullOrEmpty(l.Fid)).Select(l => l.Fid),
                StringComparer.Ordinal);
            var missing = requested.Where(f => !gotFids.Contains(f)).ToList();
            if (missing.Count == 0)
                return "";

            var sb = new StringBuilder(128);
            sb.Append("百度未返回 ").Append(missing.Count).Append('/')
              .Append(requested.Count).Append(" 个文件的直链");
            // 原始 errno/errmsg：整批层面可能就是非 0（此时 RequestJsonAsync 已抛异常，
            // 能走到这里说明批层面是 0），单文件层面的拒绝只会体现在"缺失"上
            object rawErrno = Get(res, "errno");
            object rawMsg = Get(res, "errmsg") ?? Get(res, "error_msg");
            if (rawErrno != null)
                sb.Append("；errno=").Append(AsLong(rawErrno));
            string msg = AsString(rawMsg);
            if (!string.IsNullOrEmpty(msg))
                sb.Append("，errmsg=").Append(Truncate(msg, 120));
            sb.Append("；缺失 fs_id: ").Append(string.Join(", ", missing.Take(10)));
            if (missing.Count > 10) sb.Append(" …等 ").Append(missing.Count).Append(" 个");
            sb.Append("（分享直链缺失多为文件被风控或超出可下载大小限制，"
                    + "可尝试转存到自己的网盘后下载）");
            return sb.ToString();
        }

        /// <summary>解析 sharedownload 返回的单条记录。</summary>
        private static DownloadLink ParseDlinkEntry(object o)
        {
            var it = AsDict(o);
            if (it == null) return null;
            string dlink = AsString(Get(it, "dlink"));
            if (string.IsNullOrEmpty(dlink)) return null;

            return new DownloadLink
            {
                Fid = AsString(Get(it, "fs_id")) ?? "",
                DownloadUrl = dlink,
                Name = AsString(Get(it, "server_filename")) ?? "",
                Size = AsLong(Get(it, "size")),
            };
        }

        private static DownloadLink FirstLinkFromResponse(Dictionary<string, object> res, string fid)
        {
            var list = AsArray(Get(res, "list")) ?? new object[0];
            foreach (var o in list)
            {
                var link = ParseDlinkEntry(o);
                if (link != null && (string.IsNullOrEmpty(fid) || link.Fid == fid))
                    return link;
            }
            // 部分版本 dlink 直接挂在 result.dlink
            string direct = AsString(Get(res, "dlink"))
                            ?? AsString(Get(res, "result", "dlink"));
            if (!string.IsNullOrEmpty(direct))
                return new DownloadLink { Fid = fid, DownloadUrl = direct };
            return null;
        }

        /// <summary>
        /// 【2026-10-14 晚】为**某个账号的客户端**加载分享上下文（分享页 + 提取码校验）。
        /// 多账号轮询时，每个参与的账号都要用自己的会话加载一遍 ——
        /// 之后该账号的取链/下载才带自己的登录态（直链与账号绑定）。
        /// 已加载过同一分享则重复调用也无妨（内部会重新拉页面，参数以新的一次为准）。
        /// </summary>
        public Task EnsureShareContextAsync(string surl, string passcode,
            CancellationToken cancel = default)
        {
            return LoadSharePageAsync(surl, passcode ?? "", cancel);
        }

        /// <summary>
        /// 【2026-10-14 晚】按已加载的分享上下文批量取直链（分享文件专用）。
        /// 多账号轮询的分发单元：每个账号取自己那一份文件的链。
        /// </summary>
        public Task<List<DownloadLink>> ResolveShareFidsAsync(List<string> fids,
            CancellationToken cancel = default)
        {
            return ResolveBatchAsync(fids ?? new List<string>(), cancel);
        }

        /// <summary>
        /// 读 sekey（= BDCLND Cookie 的值）。有提取码的分享必须带；
        /// 无码分享没有这颗 Cookie，返回 null（调用方据此省略参数）。
        /// </summary>
        private string GetSekey()
        {
            try
            {
                var jar = _session.Jar.GetCookies(new Uri(BaiduConstants.ApiBase));
                var ck = jar["BDCLND"];
                return ck != null ? ck.Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读 BAIDUID（logid 参数的来源，取不到返回 null）。</summary>
        private string GetBaiduid()
        {
            try
            {
                var jar = _session.Jar.GetCookies(new Uri(BaiduConstants.ApiBase));
                var ck = jar["BAIDUID"];
                return ck != null ? ck.Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 确保自己的网盘里存在这个目录（存在则无事发生）。
        /// 百度 /api/create 对已存在目录也返回 errno 0，可安全重复调用。
        /// </summary>
        private async Task EnsureDirAsync(string path, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return;
            await EnsureBdstokenAsync(cancel).ConfigureAwait(false);

            var res = await RequestFormAsync(
                AppendQuery(BaiduConstants.DirCreate, new Dictionary<string, string>
                {
                    { "a", "commit" },
                    { "bdstoken", _bdstoken ?? "" },
                }),
                new Dictionary<string, string>
                {
                    { "path", path },
                    { "isdir", "1" },
                    { "r", "0.5" },
                }, cancel).ConfigureAwait(false);

            // errno 0 = 创建成功或已存在；-6 = 登录失效（后续转存也会失败，这里先放行）
            object rawErrno = Get(res, "errno");
            int errno = rawErrno == null ? 0 : (int)AsLong(rawErrno);
            if (errno != 0 && errno != -6)
            {
                string msg = AsString(Get(res, "errmsg")) ?? "";
                throw new PanelApiException(
                    "创建网盘目录失败（" + path + "）errno=" + errno
                    + (string.IsNullOrEmpty(msg) ? "" : "：" + Truncate(msg, 100)), errno);
            }
        }

        /// <summary>
        /// 【2026-10-15】分享转存兜底：把分享里的文件保存到自己的网盘，再取直链。
        ///
        /// <para><b>解决什么：</b>百度对分享里的部分文件（版权风控的 PDF/书籍、
        /// 超大文件）会在 sharedownload 里**静默拒发直链**（errno=0 但不在返回
        /// list 里）。此时唯一的合法出路就是网页端「保存到网盘」按钮走的路：
        /// 转存到自己的网盘，再从自己网盘按 locatedownload 取链
        /// （走原生客户端身份，进客户端带宽池）。</para>
        ///
        /// <para><b>注意：</b>转存是**写操作**，会在用户网盘里占一份空间，
        /// 目标目录固定为 <see cref="BaiduConstants.DefaultFolder"/>-分享转存；
        /// ondup=newcopy 保证不覆盖同名旧文件（重名的会变成"名字(1).pdf"）。</para>
        /// </summary>
        /// <param name="shareFid">分享里的 fs_id。</param>
        /// <param name="fileName">文件名（转存后按它在新目录里认领文件）。</param>
        /// <param name="fileSize">文件大小；&gt;0 时用于同名消歧。</param>
        /// <param name="targetDir">目标目录（自己的网盘路径，如 /ZERA-分享转存）。</param>
        public async Task<DownloadLink> SaveShareFileThenLinkAsync(
            string shareFid, string fileName, long fileSize, string targetDir,
            CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(shareFid))
                throw new PanelApiException("缺少要转存的文件 fs_id");
            if (string.IsNullOrEmpty(_shareId) || string.IsNullOrEmpty(_shareUk))
                throw new PanelApiException("还没有加载分享上下文，请先解析分享链接");

            await EnsureBdstokenAsync(cancel).ConfigureAwait(false);
            if (string.IsNullOrEmpty(_bdstoken))
                throw new PanelApiException("取不到 bdstoken（CSRF 令牌），转存无法进行；请重新登录后重试");
            await EnsureDirAsync(targetDir, cancel).ConfigureAwait(false);

            // ---- 转存（网页端「保存到网盘」同一接口）----
            // query: shareid/from(uk)/sekey/bdstoken/logid + ondup/async/channel/web/app_id/clienttype
            // form:   fsidlist=[fid] + path=目标目录
            var query = new Dictionary<string, string>
            {
                { "shareid", _shareId },
                { "from", _shareUk },
                { "ondup", "newcopy" },
                { "async", "1" },
                { "channel", BaiduConstants.Channel },
                { "web", BaiduConstants.Web },
                { "app_id", BaiduConstants.AppId },
                { "bdstoken", _bdstoken },
                { "clienttype", BaiduConstants.ClientType },
            };
            string sekey = GetSekey();
            if (!string.IsNullOrEmpty(sekey))
                query["sekey"] = sekey;
            string baiduid = GetBaiduid();
            if (!string.IsNullOrEmpty(baiduid))
                query["logid"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(baiduid));

            var res = await RequestFormAsync(
                AppendQuery(BaiduConstants.ShareTransfer, query),
                new Dictionary<string, string>
                {
                    { "fsidlist", "[\"" + shareFid + "\"]" },
                    { "path", targetDir },
                }, cancel).ConfigureAwait(false);

            int errno = (int)AsLong(Get(res, "errno"));
            // 4 = 已经保存过了（ondup=newcopy 下少见，但兼容处理）→ 继续认领
            if (errno != 0 && errno != 4)
                throw new PanelApiException(ExplainErrno(errno,
                    AsString(Get(res, "errmsg")) ?? AsString(Get(res, "show_msg"))), errno);

            // ---- 轮询目标目录，按文件名认领新 fs_id ----
            // async=1 是服务端队列转存，落盘有延迟；newcopy 重名时会改名成
            // "名字(1).pdf"，所以先精确匹配、再退前缀匹配。
            string newFid = null;
            int[] delays = { 0, 700, 1200, 2000, 3000, 4500, 6500, 9000, 13000, 18000 };
            foreach (var d in delays)
            {
                if (d > 0) await Task.Delay(d, cancel).ConfigureAwait(false);
                if (cancel.IsCancellationRequested) break;
                try
                {
                    var entries = await ListDirAsync(targetDir, 1, cancel).ConfigureAwait(false);
                    newFid = PickTransferredFile(entries, fileName, fileSize);
                    if (newFid != null) break;
                }
                catch (PanelApiException)
                {
                    // 列目录瞬时失败不放弃，继续等下一轮
                }
            }
            if (newFid == null)
                throw new PanelApiException(
                    "转存请求已被百度接受，但在「" + targetDir + "」里一直没等到文件「"
                    + fileName + "」出现（异步转存可能有延迟，可稍后在自己的网盘里查看）");

            // ---- 用新 fs_id 走原生客户端取链 ----
            var link = await ResolveDriveFileAsync(newFid, cancel).ConfigureAwait(false);
            if (link == null || string.IsNullOrEmpty(link.DownloadUrl))
                throw new PanelApiException(
                    "文件已转存到自己的网盘，但取直链失败（可在「我的网盘」里手动下载）");
            link.Fid = newFid;
            link.Name = fileName;
            return link;
        }

        /// <summary>
        /// 在转存目标目录的列表里认领刚转存进来的文件。
        /// 先按名字精确匹配；重名被 newcopy 改名为「名字(1).pdf」时退前缀匹配；
        /// 多个候选时优先大小一致的，其次取最后一个（转存的最新一份排在后）。
        /// </summary>
        private static string PickTransferredFile(List<QuarkFileEntry> entries,
            string fileName, long fileSize)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(fileName))
                return null;

            var exact = entries.Where(e => e.Name == fileName).ToList();
            if (exact.Count == 1) return exact[0].Fid;
            if (exact.Count > 1)
            {
                var bySize = exact.FirstOrDefault(e => fileSize > 0 && e.Size == fileSize);
                return (bySize ?? exact[exact.Count - 1]).Fid;
            }

            // newcopy 重名：匹配 "名字(1).ext" / "名字 1.ext" 之类
            string stem = fileName;
            string ext = "";
            int dot = fileName.LastIndexOf('.');
            if (dot > 0) { stem = fileName.Substring(0, dot); ext = fileName.Substring(dot); }
            var copies = entries.Where(e =>
                e.Name.StartsWith(stem, StringComparison.Ordinal)
                && (ext.Length == 0 || e.Name.EndsWith(ext, StringComparison.Ordinal))
                && e.Name.Length > fileName.Length).ToList();
            if (copies.Count > 0)
            {
                var bySize = copies.FirstOrDefault(e => fileSize > 0 && e.Size == fileSize);
                return (bySize ?? copies[copies.Count - 1]).Fid;
            }
            return null;
        }

        /// <summary>
        /// GET 分享页并解析出 sign / timestamp / shareid / uk。
        /// 有提取码时先 POST <c>/share/verify</c> 换 sekey（落在 BDCLND Cookie 上），再重新取页面。
        ///
        /// 【顺序很关键】必须**先解析页面拿到 sign/timestamp**，才能提交提取码 ——
        /// <c>/share/verify</c> 的表单里要带这两个值。所以流程是：
        /// 第一次 GET → 先解析 → （需要码则）提交 → 第二次 GET → 再解析。
        /// 早前把两步写反导致 verify 提交时 sign 为空，返回 -9。
        /// </summary>
        private async Task LoadSharePageAsync(string surl, string passcode,
            CancellationToken cancel)
        {
            string pageUrl = BaiduConstants.SharePage + surl;

            // 记下 surl：分享目录浏览（share/list）与按分享取链都要用
            _shareSurl = surl;

            string html = await GetStringAsync(pageUrl, cancel).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                throw new PanelApiException("分享页返回空内容（可能被百度风控拦截）");

            // 先解析一次，拿到 verify 所需的 sign / timestamp
            ParseSharePage(html);

            if (!IsPasscodeRequired(html))
                return;

            // 需要提取码
            if (string.IsNullOrEmpty(passcode))
                throw new PanelApiException("该分享需要提取码，请填写后重试",
                    BaiduConstants.ErrBadPasscode);

            bool ok = await SubmitPasscodeAsync(surl, passcode, cancel).ConfigureAwait(false);
            if (!ok)
                throw new PanelApiException("提取码错误，请检查后重试",
                    BaiduConstants.ErrBadPasscode);

            // verify 成功后 Cookie 里多了 BDCLND（sekey），必须**重新取页面**：
            // 新页面的 sign/timestamp 与旧的不通用。
            html = await GetStringAsync(pageUrl, cancel).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                throw new PanelApiException("提交提取码后分享页返回空内容");
            ParseSharePage(html);
        }

        /// <summary>
        /// 提交提取码换 sekey。成功返回 true。
        ///
        /// ⚠️ 必须在 <see cref="ParseSharePage"/> 之后调用 —— 表单要带
        /// sign 与 timestamp，那是 verify 接口的必需参数。
        /// </summary>
        private async Task<bool> SubmitPasscodeAsync(string surl, string passcode,
            CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(_shareSign))
                throw new PanelApiException("未能从分享页取得 sign，无法提交提取码（百度可能已改版）");

            var form = new Dictionary<string, string>
            {
                { "pwd", passcode },
                { "vcode", "" },
                { "vcode_str", "" },
                { "surl", surl },
                { "sign", _shareSign },
                { "timestamp", _shareTimestamp.ToString(CultureInfo.InvariantCulture) },
            };

            var res = await RequestFormAsync(BaiduConstants.ShareVerify, form, cancel)
                .ConfigureAwait(false);

            int errno = (int)AsLong(Get(res, "errno"));
            if (errno == BaiduConstants.ErrNeedCaptcha)
                throw new PanelApiException(
                    "百度要求图形验证码以验证提取码。请在浏览器里打开该分享链接完成验证后再试",
                    errno, 0, needsCaptcha: true);

            // sekey 通过 Set-Cookie 下发（BDCLND），会话层已自动收进 CookieContainer
            return errno == 0;
        }

        /// <summary>
        /// 从分享页 HTML 里抠出 <c>yunData</c> 中的 sign / timestamp / shareid / uk。
        ///
        /// 百度的分享页把这些参数以一段内联 JS 的形式下发，形如
        /// <c>var yunData = { "sign": "...", "timestamp": 1234, "shareid": ..., "share_uk": ... };</c>
        /// 各版本字段名有出入（shareid / share_id、share_uk / uk），
        /// 所以两种拼写都试一遍。
        /// </summary>
        private void ParseSharePage(string html)
        {
            if (string.IsNullOrEmpty(html))
                throw new PanelApiException("分享页返回空内容（可能被百度风控拦截）");

            var block = ExtractYunData(html);

            _shareSign = AsString(Get(block, "sign")) ?? AsString(Get(block, "SIGN"));
            _shareTimestamp = AsLong(Get(block, "timestamp")) != 0
                ? AsLong(Get(block, "timestamp"))
                : AsLong(Get(block, "TIMESTAMP"));
            _shareId = AsString(Get(block, "shareid")) ?? AsString(Get(block, "share_id"))
                       ?? AsString(Get(block, "shareId"));
            _shareUk = AsString(Get(block, "share_uk")) ?? AsString(Get(block, "uk"))
                       ?? AsString(Get(block, "SHARE_UK"));

            if (string.IsNullOrEmpty(_shareId))
                throw new PanelApiException(
                    "未能从分享页解析出分享 ID。百度可能改版或对当前网络返回了风控页面，"
                    + "请稍后重试或更换网络");

            // bdstoken 也在同一段里，顺手捡上，省一次请求
            if (string.IsNullOrEmpty(_bdstoken))
            {
                string tok = AsString(Get(block, "bdstoken")) ?? AsString(Get(block, "MYBDSTOKEN"));
                if (!string.IsNullOrEmpty(tok)) _bdstoken = tok;
            }
        }

        private static readonly Regex YunData = new Regex(
            @"yunData\s*=\s*(\{.*?\});", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>抠出 yunData 对象并转成字典。</summary>
        private static Dictionary<string, object> ExtractYunData(string html)
        {
            var m = YunData.Match(html);
            if (m.Success)
            {
                try
                {
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .DeserializeObject(m.Groups[1].Value) as Dictionary<string, object>;
                }
                catch { /* 落到下面的逐字段正则 */ }
            }

            // 兜底：逐字段正则。对 JS 语法不标准、JSON 解析失败的情况更耐受。
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            AddField(d, html, @"[""']sign[""']\s*:\s*[""']([^""']+)[""']", "sign");
            AddField(d, html, @"[""']timestamp[""']\s*:\s*[""']?(\d+)[""']?", "timestamp");
            AddField(d, html, @"[""']share_?id[""']\s*:\s*[""']?([\w\-]+)[""']?", "shareid");
            AddField(d, html, @"[""']share_?uk[""']\s*:\s*[""']?([\w\-]+)[""']?", "share_uk");
            AddField(d, html, @"[""']uk[""']\s*:\s*[""']?(\d+)[""']?", "uk");
            AddField(d, html, @"[""']bdstoken[""']\s*:\s*[""']([^""']+)[""']", "bdstoken");
            return d;
        }

        private static void AddField(Dictionary<string, object> d, string html, string pattern, string key)
        {
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (m.Success) d[key] = m.Groups[1].Value;
        }

        /// <summary>
        /// 递归收集分享里的所有**文件** fs_id。
        /// 目录节点会继续展开，直到达到数量上限。
        /// </summary>
        private async Task CollectFileIdsAsync(string surl, string passcode, string dir,
            List<string> sink, int cap, CancellationToken cancel)
        {
            if (sink.Count >= cap) return;

            var query = new Dictionary<string, string>
            {
                { "shorturl", surl.Replace("/s/", "") },
                { "root", "1" },
                { "page", "1" },
                { "num", "100" },
                { "web", BaiduConstants.Web },
            };
            if (!string.IsNullOrEmpty(dir)) query["dir"] = dir;
            if (!string.IsNullOrEmpty(passcode)) query["sekey"] = passcode;

            var res = await RequestJsonAsync(HttpMethod.Get, BaiduConstants.ShareList,
                query, cancel: cancel).ConfigureAwait(false);

            var list = AsArray(Get(res, "list")) ?? new object[0];
            foreach (var o in list)
            {
                if (sink.Count >= cap) return;
                var it = AsDict(o);
                if (it == null) continue;

                bool isDir = AsLong(Get(it, "isdir")) == 1;
                string fsId = AsString(Get(it, "fs_id"));
                if (string.IsNullOrEmpty(fsId)) continue;

                if (isDir)
                {
                    await CollectFileIdsAsync(surl, passcode, fsId, sink, cap, cancel)
                        .ConfigureAwait(false);
                }
                else
                {
                    sink.Add(fsId);
                }
            }
        }

        // ================= 直链刷新（续传自愈） =================

        /// <summary>
        /// 重新取直链。
        ///
        /// 【为什么百度这个实现比较"笨"】理想做法是复现取链的全部分享上下文。
        /// 但百度的 sign/timestamp/shareid 都来自分享页的实时解析，重新走一遍
        /// 需要用户的提取码（不一定还留着）。因此这里分两种情况：
        ///   - 上下文还在（本次进程内解析过）→ 完整重走流程，效率最高；
        ///   - 上下文丢失（程序重启后）→ 退化为"请重新解析分享"。
        ///
        /// 对比夸克：夸克有 token/stoken 可持久化，所以能无缝续传。
        /// 这是平台差异，不是实现偷懒。
        /// </summary>
        public async Task<string> RefreshLinkAsync(Download.PendingInfo pending,
            CancellationToken cancel = default)
        {
            if (pending == null)
                throw new PanelApiException("缺少续传上下文");

            // 分享任务：上下文完整时可以自愈
            if (pending.FromShare && !string.IsNullOrEmpty(_shareSign) && !string.IsNullOrEmpty(_shareId))
            {
                var links = await ResolveBatchAsync(new List<string> { pending.Fid }, cancel)
                    .ConfigureAwait(false);
                if (links.Count > 0 && !string.IsNullOrEmpty(links[0].DownloadUrl))
                    return links[0].DownloadUrl;
            }

            // 我的网盘任务：凭 fs_id 重新取链即可，不依赖分享上下文
            if (!pending.FromShare && !string.IsNullOrEmpty(pending.Fid))
            {
                var link = await ResolveDriveFileAsync(pending.Fid, cancel).ConfigureAwait(false);
                if (link != null && !string.IsNullOrEmpty(link.DownloadUrl))
                    return link.DownloadUrl;
            }

            throw new PanelApiException(
                "百度直链已过期，且无法自动续传（分享上下文需重新解析）。请重新解析该分享链接");
        }

        // ================= 下载上下文 =================

        /// <summary>
        /// 组装百度下载所需的请求头。
        ///
        /// <para>【UA 策略（2026-10-14 晚重做）】</para>
        /// <list type="bullet">
        /// <item><b>下载 UA 用客户端 UA</b>（<c>netdisk;…</c>）：百度按 UA 分带宽池，
        ///       客户端池比网页端池宽松，这是非会员侧唯一可用的加速杠杆；</item>
        /// <item><b>取链（API）仍用浏览器 UA</b>：网页端接口（share/list、
        ///       sharedownload、api/list）按网页端的来，不给自己换身份；</item>
        /// <item><b>回退 UA 是浏览器 UA</b>：万一百度校验"取链与下载 UA 同族"而 403，
        ///       引擎会自动换回浏览器 UA 重试（进度保留）。社区"网页端取链 + 客户端
        ///       UA 下载"的组合长期有效，但留这条保险不赌运气。</item>
        /// </list>
        /// <para>【Referer / Origin】必须是 pan.baidu.com —— 直链在 d.pcs.baidu.com，
        /// 带错域的 Referer/Origin 本身就是异常特征。</para>
        /// </summary>
        public DownloadRequestContext BuildDownloadContext()
        {
            return new DownloadRequestContext
            {
                Cookie = _session.BuildCookieString(),
                UserAgent = BaiduConstants.DownloadUa,
                Referer = BaiduConstants.Referer,
                Origin = BaiduConstants.Origin,
                FallbackUserAgent = PanelSessionProfile.Baidu.ApiUserAgent,
                FallbackReferer = BaiduConstants.Referer,
                FallbackOrigin = BaiduConstants.Origin,
                // 非会员被服务端按账号限速（约 100-300 KB/s，客户端绕不过）。
                // 并发压到 2 连接以内：百度对普通账号的高并发会触发持续数小时~数天的
                // **惩罚性降速**（BaiduPCS-Go 排障表实测），而配额按账号下发、
                // 多连接本就不会更快 —— 保守并发是保住账号应有的速度。
                // 这里如实上报，避免上层开一堆线程却毫无收益还踩风控。
                SuggestedThreads = _isVip ? BaiduConstants.VipThreads : BaiduConstants.FreeThreads,
                MaxRequestBytes = 1024L * 1024,
                SpeedNote = (Membership?.Kind ?? "百度会员身份未确认") + "：并发建议 " +
                    (_isVip ? BaiduConstants.VipThreads : BaiduConstants.FreeThreads) +
                    "；线程数不代表极速流量已启用。实际速度由服务端、网络和文件决定。",
            };
        }

        // ================= 工具 =================

        /// <summary>从分享页取原始 HTML（不查 errno，因为未登录/需提取码时返回的也是 HTML）。</summary>
        private async Task<string> GetStringAsync(string url, CancellationToken cancel)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                using (var resp = await _session.SendWithRetryAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    // 非 API 请求：套网页端的 Accept 与 Sec-Fetch 组合
                    _session.ApplyHeaders(req, url, api: false);
                    return req;
                }, 2, cts.Token).ConfigureAwait(false))
                {
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 从任意文本里提出分享短码（surl）。
        /// 支持：完整链接 <c>https://pan.baidu.com/s/1xxxx</c>、
        /// 带提取码参数的、以及不带协议的裸串。
        /// </summary>
        public static string ExtractSurl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var m = Regex.Match(input, @"/s/([A-Za-z0-9_\-]+)",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            // 用户可能直接贴了 surl 本身
            m = Regex.Match(input, @"^([A-Za-z0-9_\-]{8,})$");
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// 从任意文本里提出提取码。
        /// 识别 URL 上的 <c>?pwd=xxxx</c> 与中文文案里的「提取码：xxxx」。
        /// </summary>
        public static string ExtractPasscode(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var m = Regex.Match(input, @"[?&]pwd=([A-Za-z0-9]{4})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            m = Regex.Match(input,
                @"(?:提取码|提取碼|密码|密碼|访问码|訪問碼|access_code)\s*[=:：]?\s*([A-Za-z0-9]{4})",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// 判断分享页 HTML 是否表示"需要提取码"。
        ///
        /// 【为什么不直接查 errno】需要提取码时，百度返回的是**一个正常页面**
        /// （内含提取码输入框），HTTP 200 且内容是 HTML，压根没有 JSON 可解析。
        /// 所以只能靠页面里的特征标记判断。各版本文案不一，故列出多个特征。
        /// </summary>
        private static bool IsPasscodeRequired(string html)
        {
            if (string.IsNullOrEmpty(html)) return false;

            string[] markers =
            {
                "请输入提取码",
                "needAccessCode",
                "accessCode\", type",
                "accessCode =",
                "\"errno\": -9",       // 极端情况下直接把 -9 渲染进页面
                "share/list?shorturl",
            };
            foreach (var mk in markers)
            {
                if (html.IndexOf(mk, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string AppendQuery(string url, IDictionary<string, string> query)
        {
            var sb = new StringBuilder(url);
            sb.Append(url.IndexOf('?') >= 0 ? '&' : '?');
            bool first = true;
            foreach (var kv in query)
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=')
                  .Append(Uri.EscapeDataString(kv.Value ?? ""));
                first = false;
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ---- JSON 取值辅助（与 QuarkClient 同风格，保持项目内一致） ----

        private static object Get(Dictionary<string, object> d, string key) => Json.Get(d, key);
        private static object Get(Dictionary<string, object> d, string k1, string k2)
            => Json.Get(Json.Get(d, k1) as Dictionary<string, object>, k2);
        private static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;
        private static object[] AsArray(object o) => o as object[];
        private static string AsString(object o) => Json.AsString(o);
        private static long AsLong(object o) => Json.AsLong(o);

        /// <summary>
        /// 释放平台专属资源。百度没有保活心跳一类的东西，空实现即可。
        ///
        /// ⚠️ 不能靠接口默认方法（net48 不支持，见 IPanelClient 的说明），
        ///    所以这里必须显式写。
        /// </summary>
        public void ShutdownPlatformResources()
        {
            // 无平台专属长期资源。百度不维持保活心跳，也不订阅 LoginExpired。
        }

        public void Dispose() => _session?.Dispose();
    }

    /// <summary>JSON 取值的共用小工具（避免与 QuarkClient 的私有方法重复实现）。</summary>
    internal static class Json
    {
        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && key != null && d.TryGetValue(key, out v))
                return v;
            return null;
        }

        public static string AsString(object o)
        {
            if (o == null) return null;
            var s = o as string;
            if (s != null) return s;
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static long AsLong(object o)
        {
            if (o == null) return 0;
            try
            {
                if (o is long) return (long)o;
                if (o is int) return (int)o;
                if (o is double) return (long)(double)o;
                if (o is decimal) return (long)(decimal)o;
                long parsed;
                if (long.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            catch { }
            return 0;
        }
    }
}
