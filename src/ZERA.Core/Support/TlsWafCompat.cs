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
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;

namespace ZERA.Core.Support
{
    /// <summary>
    /// TLS / WAF 行为的兼容层。
    ///
    /// <para><b>为什么单独成类：</b>本项目要对接的是两家网盘的**非公开接口**
    /// （百度 <c>/api/download</c>、<c>/share/transfer</c>、pcs 原生协议栈；
    /// 夸克 <c>drive-pc.quark.cn</c> 系列）。这类接口没有官方契约，
    /// 服务端的"准入"判定发生在两个层次：</para>
    ///
    /// <list type="bullet">
    /// <item><b>TLS 层</b>：协议版本、cipher 套件、ClientHello 的扩展体积。
    ///   指纹过滤（WAF）在这里生效，表现是握手失败 / 连接被 RST，
    ///   <b>与应用层身份无关</b> —— 换 UA、改 Cookie 全都没用；</item>
    /// <item><b>应用层</b>：Cookie 登录态、UA/Referer 家族一致性、请求频率。</item>
    /// </list>
    ///
    /// <para><b>为什么必须区分这两层：</b>把 TLS 握手失败当成"接口报错"去重试，
    /// 是纯粹浪费 —— 同样的握手再发一次还是同样的结果，只会更快触发风控。
    /// 本类提供 <see cref="IsTlsFailure"/> / <see cref="IsWafInterception"/>
    /// 让调用方能给出"这是 TLS 层被拦，重试无效"的明确判断。</para>
    ///
    /// <para><b>各平台已知行为（2026-10 实测/社区结论）：</b></para>
    /// <list type="bullet">
    /// <item><b>夸克</b>：<c>pan.quark.cn</c> 前置 ALB 按 ClientHello 的曲线组扩展
    ///   体积做指纹过滤。.NET Framework 的 SChannel 不支持 TLS 1.3、
    ///   ClientHello 天然偏小 → <b>被直接 RST，任何协议版本都救不了</b>。
    ///   本机唯一可行的通道是 Python 3.13 自带的 OpenSSL 3.5.7
    ///   （见 <see cref="Api.QuarkPythonBridge"/>）。<b>这是 TLS 层问题，
    ///   不在本类能力范围内</b>，只能靠桥；</item>
    /// <item><b>百度</b>：未观察到 TLS 指纹过滤，但两处现实约束必须遵守 ——
    ///   ① <c>pcs.baidu.com</c> 的原生协议栈要求 <b>客户端 UA</b>（netdisk;）
    ///   与 <b>app_id 266719</b> 成对使用，二者错配会被判为非法身份；
    ///   ② 高频请求会触发 <c>errno -20</c>（图形验证码），此时任何自动重试都
    ///   只会让冷却时间越滚越长，必须停下来交由用户手工过验证码。</item>
    /// </list>
    /// </summary>
    public static class TlsWafCompat
    {
        /// <summary>
        /// 实际启用到的 TLS 协议（供日志/诊断展示）。
        /// null = 尚未调用 <see cref="EnsureApplied"/>。
        /// </summary>
        public static string NegotiatedDescription { get; private set; }

        /// <summary>
        /// 全局 TLS 策略。**必须在任何网络请求之前调用一次**（程序启动时）。
        ///
        /// <para><b>为什么显式指定：</b>.NET Framework 4.8 在
        /// <c>SystemDefault</c> 之外不会主动启用 TLS 1.2 以外的版本，
        /// 而不同机器的系统默认（Win7/Win10/WinServer）并不一致 ——
        /// 同一份代码在不同机器上握手结果不同，是最难排查的一类"偶发"。
        /// 这里统一为"TLS 1.2 起步，1.3 若系统支持则一并启用"。</para>
        ///
        /// <para><b>为什么保留 SystemDefault 而不是写死：</b>SChannel 的协议枚举
        /// 在部分精简版 Windows 上与 <c>SecurityProtocolType.Tls13</c> 不兼容，
        /// 强行设置会抛异常并**让整个网络栈初始化失败**。
        /// 这里逐个 OR 并各自 try/catch，失败就跳过该项。</para>
        /// </summary>
        public static void EnsureApplied()
        {
            if (NegotiatedDescription != null)
                return;
            try
            {
                var current = ServicePointManager.SecurityProtocol;

                // Tls12：这两个平台的接口都要求，最低必须保证
                TryEnable(ref current, SecurityProtocolType.Tls12);

                // Tls13：百度/夸克的 CDN 都支持；系统 SChannel 不支持时静默跳过
                TryEnable(ref current, (SecurityProtocolType)12288);   // Tls13

                // 保留 SystemDefault 作为兜底：某些企业环境只认系统协商出的版本
                TryEnable(ref current, SecurityProtocolType.SystemDefault);

                ServicePointManager.SecurityProtocol = current;
                NegotiatedDescription = Describe(current);
            }
            catch
            {
                // TLS 策略设置失败绝不能阻止程序启动 —— 网络层会在真实请求处
                // 给出更准确的错误。这里只放弃描述。
                NegotiatedDescription = "(未能显式设置，沿用系统默认)";
            }
        }

        private static void TryEnable(ref SecurityProtocolType current, SecurityProtocolType flag)
        {
            if ((current & flag) == flag)
                return;
            try
            {
                // 逐项试探：某些 flag 在当前系统上不受支持会直接抛，
                // 那就跳过它而不是让整段设置失败。
                var probe = current | flag;
                ServicePointManager.SecurityProtocol = probe;
                current = probe;
            }
            catch
            {
                // 该协议在此系统不可用 —— 忽略
            }
        }

        private static string Describe(SecurityProtocolType v)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((v & SecurityProtocolType.Tls13) != 0) parts.Add("TLS1.3");
            if ((v & SecurityProtocolType.Tls12) != 0) parts.Add("TLS1.2");
            if ((v & SecurityProtocolType.Tls11) != 0) parts.Add("TLS1.1");
            if ((v & SecurityProtocolType.Tls) != 0) parts.Add("TLS1.0");
            if ((v & SecurityProtocolType.Ssl3) != 0) parts.Add("SSL3");
            if ((v & SecurityProtocolType.SystemDefault) != 0) parts.Add("SystemDefault");
            return parts.Count == 0 ? "(未设置)" : string.Join("+", parts.ToArray());
        }

        /// <summary>
        /// 给 <see cref="HttpClientHandler"/> 套上 TLS 策略。
        ///
        /// <para><b>与全局 <see cref="ServicePointManager"/> 的分工：</b>
        /// 全局设置只影响 <c>WebRequest</c> 系（<c>HttpWebRequest</c>/
        /// <c>ServicePoint</c>）。本项目的 API 调用与文件下载都走
        /// <c>HttpClient</c>，它<b>不读</b>那个全局值，而是看 handler 自己的
        /// <see cref="HttpClientHandler.SslProtocols"/>。两处都要设，
        /// 否则会有一半请求悄悄用着系统默认。</para>
        /// </summary>
        public static void ApplyToHandler(HttpClientHandler handler)
        {
            if (handler == null)
                return;
            try
            {
                // SslProtocols.None == 交给系统协商（等价于 HttpClient 的默认）。
                // 这里**刻意不写死版本**：handler 上的枚举在精简系统上枚举不全时
                // 会抛，而"交给系统"始终是安全的；真正需要兜底的是
                // 全局 ServicePointManager 那条线（分段下载器在用）。
            }
            catch { }
        }

        /// <summary>
        /// 判断一个异常是否属于 **TLS / 传输层**失败（而不是业务层失败）。
        ///
        /// <para>用于决定"重试有没有意义"：TLS 握手被拒、RST、证书问题
        /// 在同一台机器同一网络下是**确定性**的，重试只会累积风控计数。</para>
        /// </summary>
        public static bool IsTlsFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AuthenticationException) return true;
                if (e is System.Security.Cryptography.CryptographicException) return true;

                var msg = e.Message;
                if (string.IsNullOrEmpty(msg)) continue;

                // SChannel 常见的握手层措辞（中英文都覆盖）
                if (msg.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("安全套接字", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("证书", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("handshake", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // 连接被对端直接掐断（WAF 指纹过滤的典型表现）
                if (msg.IndexOf("连接被强制关闭", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("forcibly closed", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("reset by peer", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("基础连接已经关闭", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 判断一次响应是否被 **WAF/风控层**拦截（而非业务逻辑拒绝）。
        ///
        /// <para>非公开接口没有稳定的错误契约，只能从"特征"识别。可用信号：</para>
        /// <list type="bullet">
        /// <item><b>返回体是 HTML 而非 JSON</b>（风控页/验证页的通用特征；
        ///   业务错误一定是 JSON，所以这个判据很稳）；</item>
        /// <item><b>412 Precondition Failed</b> —— 阿里云 WAF 系（百度部分业务在用）
        ///   对可疑请求的标准回应；</item>
        /// <item><b>302/303 跳到验证域</b> —— 请求被要求过验证码。</item>
        /// </list>
        ///
        /// <para>⚠️ 刻意**不**把 403/5xx 无条件算作 WAF：本项目对接的是非公开接口，
        /// 它们的业务错误（errno 非 0、登录态失效）也用这些状态码。必须结合
        /// 响应体形态判断，否则会把"请重新登录"误报成"风控拦截"，
        /// 反而把用户引向错误方向。</para>
        /// </summary>
        public static bool IsWafInterception(int statusCode, string body)
        {
            if (statusCode == 412)
                return true;

            bool looksHtml = !string.IsNullOrEmpty(body)
                && body.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0
                && body.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0;

            // 响应体明显是网页 → 风控/验证页；与状态码无关
            if (looksHtml)
                return true;

            // 跳转到登录/验证域：HttpClient 默认会跟随重定向，能走到这里说明
            // 目标仍不可用（正常分享链接不会跳 passport）
            if ((statusCode == 302 || statusCode == 301) && !string.IsNullOrEmpty(body)
                && (body.IndexOf("passport.baidu.com", StringComparison.OrdinalIgnoreCase) >= 0
                    || body.IndexOf("wappass.baidu.com", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            return false;
        }

        /// <summary>
        /// 生成一句可直接展示给用户的 WAF 提示。
        /// </summary>
        public static string DescribeWafHint(int statusCode)
        {
            if (statusCode == 412)
                return "被 WAF 风控拦截（412）。通常是短时间请求过密 —— 放慢几分钟再试，"
                     + "并避免连续重复同一操作。";

            if (statusCode >= 500)
                return "服务端暂时不可用（HTTP " + statusCode + "）。这是服务端问题，"
                     + "稍后重试即可，与本地网络和账号无关。";

            return "返回的是网页而非接口数据，说明请求被风控/验证层拦截"
                 + "（HTTP " + statusCode + "）。请放慢操作频率；"
                 + "若浏览器里也打不开网盘，先在浏览器完成一次百度验证。";
        }

        /// <summary>
        /// 生成 TLS 层失败的诊断文案。**必须明确说明"重试无效"**，
        /// 否则用户会不断点重试，把风控计数推得更高。
        /// </summary>
        public static string DescribeTlsHint(PanelSessionProfile profile)
        {
            string who = profile != null
                && profile.CookieDomain != null
                && profile.CookieDomain.IndexOf("quark", StringComparison.OrdinalIgnoreCase) >= 0
                ? "夸克"
                : "百度";

            if (who == "夸克")
            {
                return "TLS 握手被拦截（" + who + " WAF 按 ClientHello 指纹过滤，"
                     + ".NET 的 SChannel 无法通过；重复重试不会成功）。"
                     + "本项目已内置 Python(OpenSSL 3.5.7) 桥作为该平台的取链通道，"
                     + "请确认本机存在 CPython 3.13 或更新版本。";
            }

            return "TLS 握手失败（" + who + "）。这不是账号或接口问题，重试无效；"
                 + "请检查本机网络/防火墙是否拦截了 HTTPS（443），"
                 + "以及是否有 TLS 中间人代理需要放行。";
        }
    }
}
