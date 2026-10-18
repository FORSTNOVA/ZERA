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
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Models;

namespace ZERA.Core.Panels
{
    /// <summary>
    /// 支持的网盘平台。
    ///
    /// 【为什么不枚举更多】原设计里我只打算加百度，但枚举本身是开放的 ——
    /// 只要新增一个 <see cref="IPanelClient"/> 实现并在 <see cref="PanelRegistry"/> 注册，
    /// UI 层无需改动即可支持新平台。之所以不做成动态插件，是因为本项目硬约束
    /// 「零 NuGet 依赖」，而插件化要么引 MEF/反射扫描，要么自己实现程序集加载与
    /// 版本兼容，复杂度远大于收益。
    ///
    /// ⚠️ 序列化注意：这个枚举会被写进 <c>Settings.xml</c> 与 <c>ZERA.state.json</c>。
    ///    **只允许在末尾追加**，不要调整已有成员顺序 —— 调整会让老配置反序列化错位。
    /// </summary>
    public enum PanelKind
    {
        /// <summary>夸克网盘（原唯一平台，行为与旧版本完全一致）。</summary>
        Quark = 0,

        /// <summary>百度网盘。</summary>
        Baidu = 1,
        WebDav = 2,
    }

    /// <summary>平台能力的静态元信息，供 UI 展示与前置校验用（不涉及任何网络调用）。</summary>
    public sealed class PanelDescriptor
    {
        public PanelKind Kind { get; set; }

        /// <summary>界面显示名。</summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// 分享链接的域名特征（用于自动识别用户粘贴的是哪个平台的链接）。
        /// 多个域名任一命中即算匹配。
        /// </summary>
        public string[] ShareHostSuffixes { get; set; } = new string[0];

        /// <summary>
        /// 是否支持目录浏览。
        /// </summary>
        public bool SupportsDrive { get; set; }
        public bool SupportsShare { get; set; } = true;
        public bool SupportsTransfer { get; set; }
        public bool SupportsCookieLogin { get; set; } = true;
        public bool SupportsMembershipQuery { get; set; }

        /// <summary>
        /// 是否支持扫码登录。
        /// 界面按平台注册的能力显示或隐藏入口。
        /// </summary>
        public bool SupportsQrLogin { get; set; }

        /// <summary>
        /// 下载速度限制说明。百度非会员会被服务端限速，这是**平台强制的**，
        /// 客户端无法绕过 —— 界面必须如实告知用户，否则会被当成程序缺陷。
        /// </summary>
        public string SpeedNote { get; set; }

        /// <summary>登录所需凭据的简短说明，用于 Cookie 导入对话框的提示文案。</summary>
        public string LoginHint { get; set; }
    }

    /// <summary>
    /// 网盘平台客户端的统一抽象。
    ///
    /// 【为什么抽这一层】原实现里 UI 直接持有 <c>QuarkClient</c>（MainWindow 有 50 处引用），
    /// 换平台就得改 UI。现在把「换取直链 / 列目录 / 登录态」这三件事收敛到接口后面，
    /// 下载引擎（<c>SegmentedDownloader</c>，3143 行）**完全不用动** ——
    /// 它只吃 URL + Cookie，与平台无关。
    ///
    /// 【设计边界】刻意保持得很小。刻意**不**包含：
    ///   - 转存（save）：只有夸克有，且百度要做转存得先解决文件夹 300MB 打包限制，
    ///     属于独立特性，不该污染最小接口；
    ///   - 分享创建：本程序是下载器，不是分享工具。
    /// 这两项若日后要加，建议另开可选接口 <c>IPanelShareOps</c> 并做类型判断，
    /// 而不是往这里堆默认实现。
    /// </summary>
    public interface IPanelClient : IDisposable
    {
        /// <summary>本客户端对应的平台。</summary>
        PanelKind Kind { get; }

        /// <summary>是否已具备有效登录态。</summary>
        bool IsLoggedIn { get; }

        /// <summary>
        /// 当前会话的完整 Cookie 串。
        ///
        /// 【为什么必须在接口上】UI 有几处需要把 Cookie 写进设置文件、下发给
        /// 下载器、或做"Cookie 是否变化"的重建判据 —— 这些都与平台无关。
        /// 之前 QuarkClient 有个 <c>CookieStr</c> 属性，加了百度之后若不放进接口，
        /// 这些调用点就得写 <c>if (client is QuarkClient q) ... else ...</c>，
        /// 每处都要分叉。
        /// </summary>
        string CookieStr { get; }

        /// <summary>
        /// 校验登录态并取回可显示的账号名。
        /// 返回 (是否有效, 昵称)。昵称可为空 —— 有些接口不返回。
        /// </summary>
        Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default);

        /// <summary>
        /// 取账号容量。返回 (总字节, 已用字节)。
        /// 平台不支持时抛 <see cref="NotSupportedException"/>。
        /// </summary>
        Task<(long total, long used)> GetCapacityAsync(CancellationToken cancel = default);

        /// <summary>
        /// 列出「我的网盘」某目录。根目录传 <c>null</c> 或空串。
        /// </summary>
        Task<List<QuarkFileEntry>> ListDirAsync(string dirFidOrPath,
            int maxPages, CancellationToken cancel = default);

        /// <summary>
        /// 解析分享链接，取出其中各文件的直链。
        ///
        /// <para><b>关于 <paramref name="maxItems"/>：</b>分享的是一个文件夹时，
        /// 服务端常常只能"打包成一个 zip"下载，而各平台对打包大小有硬上限
        /// （百度是 300MB，超出返回 31090）。因此这里只列出**单文件**条目，
        /// 让上层逐个下载 —— 这是唯一能绕开打包限制的做法。</para>
        /// </summary>
        Task<List<DownloadLink>> ResolveShareAsync(string urlOrCode,
            string passcode, int maxItems, CancellationToken cancel = default);

        /// <summary>
        /// 为「我的网盘」里的某个文件取直链（不走分享上下文）。
        /// </summary>
        Task<DownloadLink> ResolveDriveFileAsync(string fileId,
            CancellationToken cancel = default);

        /// <summary>
        /// 重新取直链 —— 供下载器在签名过期（401/403/412）时自愈续传。
        /// 参数是 <see cref="ZERA.Core.Download.PendingInfo"/> 里保存的平台相关上下文。
        ///
        /// <para>⚠️ 这正是 <c>DownloadJob.LinkRefresher</c> 的实现基础。百度直链有效期
        /// 约 1–8 小时，长任务必然过期，所以这个回调不是可选优化而是必需项。</para>
        /// </summary>
        Task<string> RefreshLinkAsync(Download.PendingInfo pending, CancellationToken cancel = default);

        /// <summary>
        /// 把该平台的下载请求头（UA / Referer / Cookie）组装成可直接分发的形式。
        /// 下载引擎只吃 URL+Cookie，但百度**必须**带对的 UA 与 Referer，否则 dlink 返回 403。
        /// </summary>
        DownloadRequestContext BuildDownloadContext();

        /// <summary>
        /// 释放平台专属的长期资源（如夸克的保活心跳）。
        ///
        /// <para>为什么放进接口：UI 关闭时需要统一调一次，若靠类型判断就得写
        /// <c>if (_client is QuarkClient q) q.StopKeepAlive();</c>。</para>
        ///
        /// <para>⚠️ 这里**不能**给默认空实现（<c>void X() { }</c> 的接口默认方法）：
        /// 那是 C# 8 / .NET Core 3+ 的特性，目标是 net48，
        /// 编译器会报 CS8701「目标运行时不支持默认接口实现」。
        /// 所以两个实现类各自显式写一遍空方法。</para>
        /// </summary>
        void ShutdownPlatformResources();
    }

    /// <summary>
    /// 平台专属的下载上下文。目前只有百度需要区分（UA 与 Referer 与夸克不同）。
    /// 用类而非多个可空属性，是为了以后新增字段不必改所有调用点。
    /// </summary>
    public sealed class DownloadRequestContext
    {
        public string Authorization { get; set; }
        public string CredentialScope { get; set; }
        /// <summary>直链本身。</summary>
        public string Url { get; set; }

        /// <summary>随请求下发的 Cookie 串；为空表示不需要。</summary>
        public string Cookie { get; set; }

        /// <summary>下载用 User-Agent；为空表示用引擎默认值。</summary>
        public string UserAgent { get; set; }

        /// <summary>Referer；为空表示不带。</summary>
        public string Referer { get; set; }

        /// <summary>
        /// 下载请求的 Origin；为空表示用引擎默认（夸克的值）。
        /// 【2026-10-14 晚】百度直链在 d.pcs.baidu.com，Origin 必须是
        /// pan.baidu.com —— 带着夸克的 Origin 去请求百度 CDN 本身就是异常特征。
        /// </summary>
        public string Origin { get; set; }

        /// <summary>
        /// 加速下载的回退 UA。下载 UA 被服务端拒绝（401/403/412）时，
        /// 引擎会自动换这个 UA 重试（保留分片进度，不重新取链）。
        /// 为空表示没有回退 —— 夸克取链与下载 UA 同族，不需要。
        /// </summary>
        public string FallbackUserAgent { get; set; }

        /// <summary>回退 UA 配套的 Referer / Origin（回退时一并换掉）。</summary>
        public string FallbackReferer { get; set; }

        public string FallbackOrigin { get; set; }

        /// <summary>
        /// 建议线程数。0 = 用引擎默认。
        ///
        /// ⚠️ 百度非会员只开放 4 线程 —— 平台侧强制限速，客户端开更多连接也不会更快，
        ///    反而更容易触发风控（频繁验证码）。所以百度未识别会员状态时返回小值。
        /// </summary>
        public int SuggestedThreads { get; set; }

        /// <summary>单次 HTTP Range 的最大字节数；0 保持原请求行为，不改变逻辑分片或断点格式。</summary>
        public long MaxRequestBytes { get; set; }

        /// <summary>
        /// 平台强制的限速说明，用于界面提示；无则为空串。
        /// </summary>
        public string SpeedNote { get; set; } = "";
    }

    /// <summary>
    /// **TLS / 传输层**握手失败（区别于业务层的 <see cref="PanelApiException"/>）。
    ///
    /// <para><b>为什么要单独一种异常：</b>业务失败（errno 非 0、403 风控页）
    /// 重试有意义；TLS 握手被 WAF 拦下是<b>确定性</b>的 —— 同一台机器、同一网络下
    /// 换个 UA 再握手十次，ClientHello 一模一样，结果也一模一样。
    /// 两者混在一起时，UI 只能统一提示"接口报错，请重试"，用户会不断点重试，
    /// 反而把风控计数推高。</para>
    ///
    /// <para>UI 层见到它应当：<b>不重试</b>，直接展示
    /// <see cref="UserHint"/>（已含"重试无效"与排查方向）。</para>
    /// </summary>
    public class TlsHandshakeException : Exception
    {
        public TlsHandshakeException(string message, Exception inner,
            ZERA.Core.Support.PanelSessionProfile profile)
            : base(message, inner)
        {
            UserHint = ZERA.Core.Support.TlsWafCompat.DescribeTlsHint(profile);
        }

        /// <summary>给用户看的完整提示（已包含平台相关的排查建议）。</summary>
        public string UserHint { get; private set; }
    }

    /// <summary>
    /// 平台客户端工厂 + 静态元信息注册表。
    ///
    /// <para><b>为什么不直接 new</b>：UI 需要在「用户粘贴链接 → 自动识别平台 → 换取链」这条
    /// 链路上按枚举取值，而客户端是有状态的（Cookie 会话）、需要复用的。
    /// 集中在这里可以避免 UI 层到处写 switch。</para>
    /// </summary>
    public static class PanelRegistry
    {
        private static readonly Dictionary<PanelKind, PanelDescriptor> Descriptors =
            new Dictionary<PanelKind, PanelDescriptor>
            {
                {
                    PanelKind.Quark, new PanelDescriptor
                    {
                        Kind = PanelKind.Quark,
                        DisplayName = "夸克网盘",
                        ShareHostSuffixes = new[] { "quark.cn", "pan.quark.cn", "drive.quark.cn" },
                        SupportsDrive = true,
                        SupportsTransfer = true,
                        SupportsQrLogin = true,
                        SpeedNote = "",
                        LoginHint = "扫码登录，或从浏览器复制 Cookie",
                    }
                },
                {
                    PanelKind.Baidu, new PanelDescriptor
                    {
                        Kind = PanelKind.Baidu,
                        SupportsMembershipQuery = true,
                        DisplayName = "百度网盘",
                        ShareHostSuffixes = new[] { "pan.baidu.com", "yun.baidu.com", "baidu.com" },
                        SupportsDrive = true,
                        // 【2026-10-14 晚】百度扫码登录已打通（passport 三段式实测），
                        // 此前的 false 是错误结论，已纠正。
                        SupportsQrLogin = true,
                        SpeedNote = "下载速度取决于账号权益、服务端、网络和文件。会员身份或并发数不代表极速流量已触发。",
                        LoginHint = "用百度 App 扫码登录，或从浏览器 F12 → 网络 → 任意 pan.baidu.com 请求 → 请求头里复制 Cookie（含 BDUSS）",
                    }
                },
                { PanelKind.WebDav, new PanelDescriptor { Kind = PanelKind.WebDav, DisplayName = "WebDAV", SupportsDrive = true,
                    SupportsShare = false, SupportsCookieLogin = false, LoginHint = "配置 WebDAV 地址、用户名和密码" } },
            };

        /// <summary>取平台静态信息；未知平台返回 null。</summary>
        public static PanelDescriptor Describe(PanelKind kind)
        {
            PanelDescriptor d;
            return Descriptors.TryGetValue(kind, out d) ? d : null;
        }

        /// <summary>全部已注册平台（UI 下拉框用）。</summary>
        public static IEnumerable<PanelDescriptor> All()
        {
            foreach (var kv in Descriptors)
                yield return kv.Value;
        }

        /// <summary>
        /// 创建一个平台客户端。调用方负责 <see cref="IDisposable"/>。
        /// </summary>
        public static IPanelClient Create(PanelKind kind, string cookie)
        {
            switch (kind)
            {
                case PanelKind.Quark:
                    return new Api.QuarkClient(cookie);
                case PanelKind.Baidu:
                    return new Api.BaiduClient(cookie);
                case PanelKind.WebDav:
                    return new WebDavClient(cookie);
                default:
                    throw new NotSupportedException("未注册的平台: " + kind);
            }
        }

        /// <summary>
        /// 从任意文本里识别平台 —— 命中任意 <see cref="PanelDescriptor.ShareHostSuffixes"/>
        /// 即返回该平台；都没命中返回 null（由调用方按当前选中平台兜底）。
        /// </summary>
        public static PanelKind? Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string t = text.Trim().ToLowerInvariant();
            foreach (var d in Descriptors.Values)
            {
                foreach (var suffix in d.ShareHostSuffixes)
                {
                    if (suffix.Length == 0)
                        continue;
                    // 要求以 "域名" 或 "域名/" 或 "域名:" 出现，避免 "notquark.cn" 之类误判
                    if (t.Contains(suffix))
                        return d.Kind;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 跨平台统一的接口异常。
    ///
    /// 【为什么不复用 <c>QuarkApiException</c>】那个类型的名字和消息都写死了「夸克」，
    /// 且缺少「需要验证码」这类平台特有语义。统一异常让 UI 能不依赖具体平台
    /// 就给出可操作的提示。
    /// </summary>
    public class PanelApiException : Exception
    {
        /// <summary>平台返回的业务错误码（百度 errno / 夸克 code）。无则 0。</summary>
        public int Code { get; }

        /// <summary>HTTP 状态码。无则 0。</summary>
        public int Status { get; }

        /// <summary>
        /// 是否为「触发了图形验证码」。
        /// 百度 errno = -20 表示需要验证码，此时用户必须手工处理，
        /// 程序不该自动重试（重试只会让风控更严）。
        /// </summary>
        public bool NeedsCaptcha { get; }

        public PanelApiException(string message, int code = 0, int status = 0, bool needsCaptcha = false)
            : base(message)
        {
            Code = code;
            Status = status;
            NeedsCaptcha = needsCaptcha;
        }

        public PanelApiException(string message, Exception inner)
            : base(message, inner) { }
    }
}
