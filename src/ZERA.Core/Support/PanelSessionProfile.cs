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

namespace ZERA.Core.Support
{
    /// <summary>
    /// 一个平台的「会话画像」：Cookie 域、请求头指纹、节流节奏。
    ///
    /// 【为什么抽出来】原 <c>QuarkSession</c> 把 `.quark.cn`、`QuarkConstants.ApiUa`、
    /// `https://pan.quark.cn` 这些**平台特有的值直接写死在方法里**，
    /// 于是百度无法复用它那套已经调好的节流 / 退避 / 连接复用逻辑 ——
    /// 要么复制一份（此后两处各自腐化），要么把值变成可配置。
    ///
    /// 选择后者。这样节流、退避、Cookie 解析、连接池这些**真正难调的部分只有一份实现**，
    /// 新增平台只需提供一组取值。
    ///
    /// ⚠️ 夸克的默认值与改造前**逐字节一致** —— 节流 260ms、UA、Referer、
    ///    Cookie 域列表都没动，避免引入回归。
    /// </summary>
    public sealed class PanelSessionProfile
    {
        /// <summary>Cookie 归属域（含前导点），种入 CookieContainer 时用。</summary>
        public string CookieDomain { get; set; }

        /// <summary>
        /// 汇总/查询 Cookie 时逐个尝试的 URL。
        /// 用完整 URL 而不只是 CookieDomain，是因为 CookieContainer 按 host 匹配，
        /// 不带 scheme 的裸域在部分平台上取不到值。
        /// </summary>
        public string[] CookieProbeUrls { get; set; }

        /// <summary>接口请求用的 UA。</summary>
        public string ApiUserAgent { get; set; }

        /// <summary>
        /// 下载直链用的 UA。留空表示与 <see cref="ApiUserAgent"/> 相同。
        ///
        /// 【为什么夸克要单独一个】夸克 CDN 会校验「取链时声明的客户端身份」与
        /// 「实际下载时的身份」是否同族，取链用客户端 UA、下载换浏览器 UA 等于自证
        /// 非客户端，更容易被判为盗链（详见 QuarkConstants.DlUa 的长注释）。
        /// 百度没有这个分层，用同一个浏览器 UA 即可。
        /// </summary>
        public string DownloadUserAgent { get; set; }

        /// <summary>接口请求的 Referer。</summary>
        public string Referer { get; set; }

        /// <summary>接口请求的 Origin；为空则不发该头。</summary>
        public string Origin { get; set; }

        /// <summary>
        /// <see cref="Referer"/> 去掉末尾斜杠后的裸域，用于判断 Sec-Fetch-Site
        /// 是否为 same-origin。留空则所有请求都按 same-site 发（夸克沿用旧行为）。
        /// </summary>
        public string SameOriginHost { get; set; }

        /// <summary>两次请求最小间隔（毫秒）。</summary>
        public int MinIntervalMs { get; set; } = 260;

        /// <summary>夸克画像 —— 与改造前完全一致。</summary>
        public static PanelSessionProfile Quark { get; } = new PanelSessionProfile
        {
            CookieDomain = ".quark.cn",
            CookieProbeUrls = new[]
            {
                "https://pan.quark.cn", "https://drive-pc.quark.cn", "https://drive-m.quark.cn",
                "https://drive.quark.cn", "https://drive-h.quark.cn", "https://uop.quark.cn",
                "https://su.quark.cn", "https://quark.cn",
            },
            ApiUserAgent = QuarkConstants.ApiUa,
            DownloadUserAgent = QuarkConstants.DlUa,
            Referer = QuarkConstants.Referer,
            Origin = QuarkConstants.Origin,
            SameOriginHost = "",
            MinIntervalMs = 260,
        };

        /// <summary>
        /// 百度画像。
        ///
        /// 【UA 分层（2026-10-14 晚重做）】此前的注释断言"百度校验取链 UA 与下载 UA
        /// 必须同族，混用会 403" —— 那条结论**未经实测**（本机没有百度会员账号，
        /// 百度下载链路从未跑通过），而社区证据正好相反：IDM / Motrix / aria2 配
        /// 「网页端解析脚本取链 + 客户端 UA 下载」是长期有效的通行做法。
        /// 因此现在的策略是：
        ///   · <see cref="ApiUserAgent"/>（取链/接口）= 桌面 Chrome UA —— 网页端身份；
        ///   · 下载 UA = <see cref="ZERA.Core.Api.BaiduConstants.DownloadUa"/>（netdisk; 客户端 UA）
        ///     —— 服务端按 UA 分带宽池，客户端池对非会员更宽松（加速下载的支点）；
        ///   · 万一 403，引擎自动回退到 <see cref="ApiUserAgent"/> 重试（进度保留）。
        ///
        /// 【节流 600ms，比夸克保守】百度对短时间高频请求的容忍度明显更低：
        /// 触发风控后返回 <c>errno = -20</c> 要走图形验证码，而验证码只能由用户手工输入，
        /// 自动重试只会让冷却时间越滚越长。宁可慢一点。
        /// </summary>
        public static PanelSessionProfile Baidu { get; } = new PanelSessionProfile
        {
            CookieDomain = ".baidu.com",
            CookieProbeUrls = new[]
            {
                "https://pan.baidu.com", "https://yun.baidu.com", "https://passport.baidu.com",
                "https://d.pcs.baidu.com", "https://baidu.com",
            },
            ApiUserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            Referer = "https://pan.baidu.com/",
            Origin = "https://pan.baidu.com",
            SameOriginHost = "pan.baidu.com",
            MinIntervalMs = 600,
        };

        /// <summary>按平台取画像。</summary>
        public static PanelSessionProfile For(Panels.PanelKind kind)
        {
            switch (kind)
            {
                case Panels.PanelKind.Baidu: return Baidu;
                case Panels.PanelKind.Quark:
                default: return Quark;
            }
        }
    }
}
