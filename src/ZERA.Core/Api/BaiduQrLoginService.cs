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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ZERA.Core.Support;

namespace ZERA.Core.Api
{
    /// <summary>百度扫码登录阶段。与夸克的 <see cref="QrLoginStage"/> 同构，便于界面复用同一套状态展示。</summary>
    public enum BaiduQrStage
    {
        /// <summary>等待用户扫码（unicast 返回 <c>errno=1</c>）。</summary>
        WaitingScan,
        /// <summary>已扫码，等待手机上点确认（<c>channel_v.status</c> 非 0）。</summary>
        ScannedWaitConfirm,
        /// <summary>已确认，拿到临时 bduss，可以兑换登录态了。</summary>
        Confirmed,
        /// <summary>二维码失效或接口报错，本轮立刻结束，不再空转到超时。</summary>
        Expired,
    }

    /// <summary>一次轮询的结果：阶段 + 原始 errno + 临时 bduss（已确认时）。</summary>
    public struct BaiduQrPollResult
    {
        public BaiduQrStage Stage;
        /// <summary>unicast 返回的原始 errno（未识别时原样带给界面，不吞信息）。</summary>
        public long Errno;
        /// <summary>失败原因（面向用户，简短；成功时通常为空）。</summary>
        public string Error;
        /// <summary>已确认时的临时 bduss，拿去换正式登录态。</summary>
        public string TempBduss;
    }

    /// <summary>
    /// 百度网盘扫码登录：轮询 passport 接口换取 BDUSS 登录 Cookie，不呼出内置浏览器。
    ///
    /// <para>【2026-10-14 接口核查结论 —— 此前"百度没有扫码通道"的判断是错的】</para>
    /// <para>百度扫码登录是完整可编程的三段式（均已实测，HTTP 200）：</para>
    /// <list type="number">
    /// <item><c>GET /v2/api/getqrcode?lp=pc</c> → 返回 <c>sign</c>（会话 ID）与 <c>imgurl</c>（二维码 PNG）；</item>
    /// <item><c>GET /channel/unicast?channel_id=&lt;sign&gt;</c> → <b>长轮询</b>：服务端 hold 约 30 秒，
    ///       未扫码返回 <c>{"errno":1}</c>；扫码后 <c>errno=0</c> 且 <c>channel_v</c> 里带状态；
    ///       用户点确认后 <c>channel_v.status=0</c>，其 <c>v</c> 字段就是临时 bduss；</item>
    /// <item><c>GET /v3/login/main/qrbdusslogin?bduss=&lt;v&gt;</c> → <c>Set-Cookie</c> 下发
    ///       <b>BDUSS</b>（十年期登录态），body 里还有 userName / portraitUrl 等。</item>
    /// </list>
    ///
    /// <para>【与夸克路线的本质差异：不需要 Python 桥】</para>
    /// <para>
    /// 夸克 <c>pan.quark.cn</c> 的前置 ALB 按 TLS ClientHello 指纹过滤，.NET SChannel 必被 RST，
    /// 只能借内置 Python（OpenSSL 3.5.7）绕路（见 <see cref="QuarkPythonBridge"/>）。
    /// 而 <c>passport.baidu.com</c> 在 .NET SChannel 下**完全可达**（2026-10-14 同机实测：
    /// getqrcode / qrcode 图片 / unicast 长轮询全部正常），所以本服务走纯 .NET 路径即可。
    /// </para>
    ///
    /// <para>【轮询节奏】unicast 是长轮询而非短轮询：每次请求服务端会 hold 到状态变化或约 30 秒超时。
    /// 因此这里一次只挂一个请求（客户端超时 45s &gt; 服务端 30s），返回后立刻再发——
    /// 既是浏览器原生的行为，也把请求频率降到最低（约每 30 秒一次），不给风控添麻烦。</para>
    /// </summary>
    public class BaiduQrLoginService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly CookieContainer _jar;

        public BaiduQrLoginService()
        {
            _jar = new CookieContainer { Capacity = 500, PerDomainCapacity = 200 };
            var handler = new HttpClientHandler
            {
                CookieContainer = _jar,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None,
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// 第一步：创建扫码会话。
        /// 成功时同时把二维码 PNG 字节取回来（百度直接给图，**不需要自己生成二维码**），
        /// 界面拿字节就能显示，和夸克"本地画码"是两条不同的展示路径。
        /// </summary>
        public async Task<(bool ok, string sign, string imgUrl, byte[] png, string error)> GetQrCodeAsync()
        {
            try
            {
                string raw = await GetStringAsync(BaiduConstants.QrGetCode).ConfigureAwait(false);
                var obj = Deserialize(raw);
                if (obj == null)
                    return (false, "", "", null, "获取扫码登录二维码失败: 返回不是 JSON");

                string sign = AsString(Get(obj, "sign")) ?? "";
                string imgUrl = AsString(Get(obj, "imgurl")) ?? "";
                if (string.IsNullOrEmpty(sign) || string.IsNullOrEmpty(imgUrl))
                    return (false, "", "", null, "获取扫码登录二维码失败: 返回缺少 sign/imgurl");

                // imgurl 形如 "passport.baidu.com/v2/api/qrcode?sign=xxx&lp=pc"，缺协议头
                string full = imgUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? imgUrl
                    : "https://" + imgUrl;

                byte[] png = await GetBytesAsync(full).ConfigureAwait(false);
                if (png == null || png.Length == 0)
                    return (false, "", "", null, "二维码图片下载失败（" + full + "）");

                return (true, sign, full, png, "");
            }
            catch (Exception ex)
            {
                return (false, "", "", null, "扫码登录接口连接失败: " + RootMessage(ex));
            }
        }

        /// <summary>
        /// 第二步：长轮询会话状态直到用户确认 / 二维码失效 / 超时。
        /// 每轮通过 <paramref name="onState"/> 把"当前处于哪一步"上报给界面。
        /// </summary>
        public async Task<BaiduQrPollResult> PollForConfirmAsync(
            string sign, int timeoutSeconds = BaiduConstants.QrSessionTimeoutSeconds,
            IProgress<BaiduQrPollResult> onState = null,
            CancellationToken cancel = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            long lastReported = long.MinValue;
            int consecutiveFailures = 0;

            while (DateTime.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();

                long errno = -1;
                string channelV = null;
                bool transportOk = true;
                string transportError = null;
                try
                {
                    string url = BaiduConstants.QrUnicast
                        + "?channel_id=" + Uri.EscapeDataString(sign)
                        + "&callback=tangram_guid_" + NowMs()
                        + "&gid=" + Guid.NewGuid().ToString("D")
                        + "&tt=" + NowMs()
                        + "&_=" + NowMs();
                    string raw = await GetStringAsync(url, BaiduConstants.QrPollTimeoutSeconds, cancel)
                        .ConfigureAwait(false);
                    var obj = DeserializeJsonp(raw);
                    if (obj == null)
                    {
                        transportOk = false;
                        transportError = "返回无法解析";
                    }
                    else
                    {
                        // ⚠️ errno 是 JSON 数字，必须走 GetObj（对象）而不是 Get（字符串），
                        // 否则 `as string` 把数字读成 null，状态机全乱。
                        errno = AsLong(GetObj(obj, "errno"));
                        channelV = AsString(GetObj(obj, "channel_v"));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单次轮询失败（网络抖动 / 服务端重置长连接）继续下一轮，
                    // 但连续多次失败就停下报错，避免无限空转。
                    transportOk = false;
                    transportError = RootMessage(ex);
                }

                if (!transportOk)
                {
                    if (++consecutiveFailures >= 5)
                    {
                        return Fail("轮询连续失败（" + transportError + "），请重新获取二维码");
                    }
                    await Task.Delay(1500, cancel).ConfigureAwait(false);
                    continue;
                }
                consecutiveFailures = 0;

                // 1) 有结果 → 解析 channel_v（它是嵌在 JSON 里的 JSON 字符串）
                if (errno == BaiduConstants.QrErrHasResult && !string.IsNullOrEmpty(channelV))
                {
                    var cv = Deserialize(channelV);
                    // status 同样是 JSON 数字，走 GetObj
                    long status = AsLong(GetObj(cv, "status"));
                    string tempBduss = AsString(Get(cv, "v")) ?? "";

                    if (status == 0 && !string.IsNullOrEmpty(tempBduss))
                    {
                        var ok = new BaiduQrPollResult
                        {
                            Stage = BaiduQrStage.Confirmed, Errno = errno, TempBduss = tempBduss,
                        };
                        Report(onState, ok);
                        return ok;
                    }

                    // 已扫码但没点确认：继续等（同一状态不重复上报，避免界面闪烁）
                    if (errno != lastReported)
                    {
                        lastReported = errno;
                        Report(onState, new BaiduQrPollResult
                        {
                            Stage = BaiduQrStage.ScannedWaitConfirm, Errno = errno,
                        });
                    }
                    continue;
                }

                // 2) 还没人扫码
                if (errno == BaiduConstants.QrErrWaitingScan)
                {
                    if (errno != lastReported)
                    {
                        lastReported = errno;
                        Report(onState, new BaiduQrPollResult
                        {
                            Stage = BaiduQrStage.WaitingScan, Errno = errno,
                        });
                    }
                    continue;
                }

                // 3) 其它 errno（含空响应/解析失败）：原样带回，不假装还在等待
                return Fail("二维码状态异常（errno=" + errno + "），请重新获取二维码");
            }

            return Fail("扫码超时（" + timeoutSeconds + " 秒），二维码可能已失效");
        }

        /// <summary>
        /// 第三步：用临时 bduss 兑换正式登录态，并补全 pan.baidu.com 域的 Cookie。
        ///
        /// <para>【为什么要 enrich】<c>qrbdusslogin</c> 只下发 BDUSS；而网盘接口还要
        /// BDCLND（CSRF 令牌）等，它们是在访问 <c>pan.baidu.com</c> 页面/接口时
        /// 由 <c>Set-Cookie</c> 下发的。所以换到 BDUSS 后，再依次访问首页与
        /// <c>gettemplatevariable</c>，把 .baidu.com 域的 Cookie 收齐，
        /// 和夸克登录后的 enrich 步骤同一个思路。</para>
        /// </summary>
        public async Task<(bool ok, string cookie, string error)> ExchangeAsync(string tempBduss)
        {
            if (string.IsNullOrWhiteSpace(tempBduss))
                return (false, "", "临时 bduss 为空");

            try
            {
                // 1) 换 BDUSS（Set-Cookie 下发）
                string url = BaiduConstants.QrBdussLogin
                    + "?bduss=" + Uri.EscapeDataString(tempBduss)
                    + "&tpl=netdisk&v=" + NowMs();
                string body = await GetStringAsync(url, 25, CancellationToken.None).ConfigureAwait(false);

                // Set-Cookie 是正路；万一没拿到，再从 body 里捞一次（body 是畸形 JSONP，
                // 但有 "bduss":"..." 这样的字段，正则足以兜底）。
                if (!JarHas(BaiduConstants.LoginCookieKey))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        body ?? "", @"""bduss""\s*:\s*""([^""]+)""");
                    if (m.Success)
                    {
                        TrySetCookie(BaiduConstants.LoginCookieKey, m.Groups[1].Value);
                    }
                }

                if (!JarHas(BaiduConstants.LoginCookieKey))
                    return (false, "", "换取登录态失败：未取回 " + BaiduConstants.LoginCookieKey);

                // 2) enrich：访问网盘域，收齐 BDCLND 等接口必需 Cookie
                string[] enrich =
                {
                    BaiduConstants.ApiBase + "/",
                    BaiduConstants.TokenApi,
                };
                foreach (var u in enrich)
                {
                    try { await GetStringAsync(u, 20, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 单次 enrich 失败不阻断：BDUSS 已经到手，接口用到时还会再下 */ }
                }

                string cookie = JoinJarCookies();
                if (string.IsNullOrEmpty(cookie))
                    return (false, "", "登录态 Cookie 汇总为空");

                return (true, cookie, "");
            }
            catch (Exception ex)
            {
                return (false, "", "换取登录态失败: " + RootMessage(ex));
            }
        }

        /// <summary>一条龙：取码 → 轮询 → 换登录态。供界面直接调用。</summary>
        public async Task<(bool ok, string cookie, string error)> LoginAsync(
            Func<(bool ok, string sign, string imgUrl, byte[] png, string error), Task> onQrReady,
            IProgress<BaiduQrPollResult> onState = null,
            CancellationToken cancel = default)
        {
            var qr = await GetQrCodeAsync().ConfigureAwait(false);
            if (!qr.ok) return (false, "", qr.error);

            if (onQrReady != null) await onQrReady(qr).ConfigureAwait(false);

            var poll = await PollForConfirmAsync(qr.sign,
                BaiduConstants.QrSessionTimeoutSeconds, onState, cancel).ConfigureAwait(false);
            if (poll.Stage != BaiduQrStage.Confirmed)
                return (false, "", poll.Error ?? "扫码未完成");

            return await ExchangeAsync(poll.TempBduss).ConfigureAwait(false);
        }

        // ---------------- 传输 ----------------

        private async Task<string> GetStringAsync(string url)
        {
            return await GetStringAsync(url, 25, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<string> GetStringAsync(string url, int timeoutSeconds, CancellationToken cancel)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", PanelSessionProfile.Baidu.ApiUserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                using (var cts = CancellationTokenSource
                    .CreateLinkedTokenSource(cancel))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    using (var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task<byte[]> GetBytesAsync(string url)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", PanelSessionProfile.Baidu.ApiUserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "image/png, image/*, */*");
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                using (var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        // ---------------- Cookie 罐操作 ----------------

        private bool JarHas(string name)
        {
            try
            {
                foreach (Cookie c in _jar.GetCookies(new Uri(BaiduConstants.PassportBase)))
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return true;
                foreach (Cookie c in _jar.GetCookies(new Uri(BaiduConstants.ApiBase)))
                    if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        private void TrySetCookie(string name, string value)
        {
            try
            {
                // passport 域下补一个等价 Cookie，供后续 pan.baidu.com 请求携带
                _jar.Add(new Cookie(name, value, "/", ".baidu.com"));
            }
            catch { }
        }

        private string JoinJarCookies()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseUrl in new[] { BaiduConstants.PassportBase, BaiduConstants.ApiBase })
            {
                try
                {
                    foreach (Cookie c in _jar.GetCookies(new Uri(baseUrl)))
                        dict[c.Name] = c.Value;
                }
                catch { }
            }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        // ---------------- 工具 ----------------

        private static BaiduQrPollResult Fail(string message)
        {
            return new BaiduQrPollResult { Stage = BaiduQrStage.Expired, Errno = -1, Error = message };
        }

        private static void Report(IProgress<BaiduQrPollResult> onState, BaiduQrPollResult r)
        {
            if (onState == null) return;
            try { onState.Report(r); } catch { }
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        /// <summary>解析普通 JSON。</summary>
        private static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        /// <summary>
        /// 解析 JSONP：形如 <c>tangram_guid_123({"errno":1})</c>。
        /// 取第一个 '{' 到最后一个 '}' —— 必须取**最后一个** '}'，
        /// 因为 errno=0 时 channel_v 里嵌着转义的 JSON 字符串，中途的 '}' 属于内层。
        /// </summary>
        private static Dictionary<string, object> DeserializeJsonp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            int start = s.IndexOf('{');
            int end = s.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return Deserialize(s.Substring(start, end - start + 1));
        }

        private static string Get(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v)) return v as string;
            return null;
        }

        private static object GetObj(Dictionary<string, object> d, string key)
        {
            if (d != null && d.TryGetValue(key, out var v)) return v;
            return null;
        }

        private static string AsString(object o) => o as string;

        private static long AsLong(object o)
        {
            if (o is long) return (long)o;
            if (o is int) return (int)o;
            if (o is double) return (long)(double)o;
            if (o is string s) { long v; return long.TryParse(s, out v) ? v : 0; }
            return 0;
        }

        /// <summary>取最内层异常的消息 —— 外层往往只是"发送请求时出错"，真正原因在里面。</summary>
        private static string RootMessage(Exception ex)
        {
            var e = ex;
            int guard = 0;
            while (e.InnerException != null && guard++ < 8)
                e = e.InnerException;
            return e.GetType().Name + ": " + e.Message;
        }

        public void Dispose() => _http.Dispose();
    }
}
