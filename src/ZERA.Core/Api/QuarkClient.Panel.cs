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
using ZERA.Core.Download;
using ZERA.Core.Models;
using ZERA.Core.Panels;

namespace ZERA.Core.Api
{
    /// <summary>
    /// 让 <see cref="QuarkClient"/> 实现 <see cref="IPanelClient"/> 的适配层。
    ///
    /// 【为什么不直接把接口方法写进 QuarkClient】两个原因：
    ///   <list type="number">
    ///   <item>
    ///   <b>签名已定型且被大量调用</b>：<c>ValidateAsync()</c> /
    ///   <c>ListDirAsync(fid, maxPages)</c> 是 MainWindow 大量调用的入口，
    ///   改成接口要求的 <c>(..., CancellationToken)</c> 会波及整条调用链。
    ///   适配器把这些差异吃在本文件里，原始方法一行不动。</item>
    ///   <item>
    ///   <b>隔离风险</b>：夸克是当前唯一经过完整验证的平台。让它通过一层薄适配器
    ///   接入多平台抽象，比直接改动它的公开 API 更安全 —— 出问题只需回退本文件。</item>
    ///   </list>
    /// </summary>
    public sealed partial class QuarkClient : IPanelClient
    {
        // 这三个成员在主文件里已经存在（private），这里只补接口要求的属性与方法。

        PanelKind IPanelClient.Kind => PanelKind.Quark;

        /// <summary>接口要求的 Cookie 访问器 —— 复用主类里已有的同名公开属性。</summary>
        string IPanelClient.CookieStr => CookieStr;

        // ---- 接口方法实现（全部转调原方法，不改动其行为） ----

        /// <summary>
        /// 统一入口：校验登录态。
        /// 原方法没有 CancellationToken 参数，这里传入后被忽略 ——
        /// 底层 <c>QuarkSession</c> 的每个请求各自有超时（30s），
        /// 取消令牌无处可挂。属于已知的小缺口，不影响功能。
        /// </summary>
        Task<(bool ok, string nick)> IPanelClient.ValidateAsync(CancellationToken cancel)
            => ValidateAsync();

        Task<(long total, long used)> IPanelClient.GetCapacityAsync(CancellationToken cancel)
            => GetCapacityAsync();

        Task<List<QuarkFileEntry>> IPanelClient.ListDirAsync(string dirFidOrPath,
            int maxPages, CancellationToken cancel)
            => ListDirAsync(dirFidOrPath, maxPages);

        /// <summary>
        /// 优雅关闭夸克专属资源（保活心跳）。非夸克平台是空实现。
        /// UI 在 Dispose 前统一调用，避免调用点写平台判断。
        /// </summary>
        void IPanelClient.ShutdownPlatformResources()
        {
            try { StopKeepAlive(); } catch { }
        }

        /// <summary>
        /// 取直链。<paramref name="urlOrCode"/> 传分享 ID 或链接，
        /// <paramref name="passcode"/> 是提取码。
        ///
        /// ⚠️ 夸克的分享解析需要 pwdId + stoken，而 stoken 必须先调
        /// <c>GetStokenAsync</c> 换取。这里在适配层内完成两步串联，
        /// 让调用方只需给出链接与提取码。
        /// </summary>
        async Task<List<DownloadLink>> IPanelClient.ResolveShareAsync(string urlOrCode,
            string passcode, int maxItems, CancellationToken cancel)
        {
            var info = Support.ShareUrlParser.Parse(urlOrCode);
            if (string.IsNullOrEmpty(info.PwdId))
                throw new PanelApiException("不是有效的夸克分享链接");
            if (info.IsKouling)
                throw new PanelApiException("这是夸克口令，无法直接解析，请让分享者提供完整链接");

            string pwd = !string.IsNullOrEmpty(passcode) ? passcode : info.Passcode;
            string stoken = await GetStokenAsync(info.PwdId, pwd).ConfigureAwait(false);

            var files = await ShareDetailAsync(info.PwdId, stoken,
                string.IsNullOrEmpty(info.StartFid) ? "0" : info.StartFid).ConfigureAwait(false);

            // 只保留文件（跳过目录），与百度"逐个文件下载"的做法一致
            var ids = new List<string>();
            var tokens = new List<string>();
            foreach (var f in files)
            {
                if (f.IsDir) continue;
                ids.Add(f.Fid);
                tokens.Add(f.Token);
                if (ids.Count >= maxItems) break;
            }
            if (ids.Count == 0)
                throw new PanelApiException("分享里没有找到可下载的文件");

            return await GetShareDownloadAsync(ids, tokens, info.PwdId, stoken)
                .ConfigureAwait(false);
        }

        /// <summary>「我的网盘」取链 —— 直接转调 <c>GetDownloadAsync</c>。</summary>
        async Task<DownloadLink> IPanelClient.ResolveDriveFileAsync(string fileId,
            CancellationToken cancel)
        {
            var list = await GetDownloadAsync(new List<string> { fileId }).ConfigureAwait(false);
            if (list == null || list.Count == 0)
                throw new PanelApiException("获取直链失败");
            return list[0];
        }

        /// <summary>
        /// 直链刷新（供下载器在签名过期时自愈续传）。
        ///
        /// 夸克能做得比百度好：<see cref="PendingInfo"/> 里保存了
        /// pwdId / stoken / token / fid，**无需用户重新解析分享**就能换到新链。
        /// 这正是百度做不到、而夸克能做到的地方 —— 差异来源于平台机制，
        /// 不是实现质量。
        /// </summary>
        async Task<string> IPanelClient.RefreshLinkAsync(PendingInfo pending,
            CancellationToken cancel)
        {
            if (pending == null || string.IsNullOrEmpty(pending.Fid))
                throw new PanelApiException("缺少续传上下文");

            if (!pending.FromShare)
            {
                var list = await GetDownloadAsync(new List<string> { pending.Fid }).ConfigureAwait(false);
                if (list != null && list.Count > 0 && !string.IsNullOrEmpty(list[0].DownloadUrl))
                    return list[0].DownloadUrl;
                throw new PanelApiException("获取直链失败");
            }

            // 分享模式：需要 stoken。若持久化上下文里有就直接用，否则重新换
            string stoken = pending.Stoken;
            if (string.IsNullOrEmpty(stoken))
            {
                if (string.IsNullOrEmpty(pending.PwdId))
                    throw new PanelApiException("缺少分享上下文，需重新解析分享链接");
                stoken = await GetStokenAsync(pending.PwdId, pending.Passcode ?? "")
                    .ConfigureAwait(false);
            }

            var shareLinks = await GetShareDownloadAsync(
                new List<string> { pending.Fid },
                new List<string> { pending.Token ?? "" },
                pending.PwdId, stoken).ConfigureAwait(false);

            if (shareLinks != null && shareLinks.Count > 0 &&
                !string.IsNullOrEmpty(shareLinks[0].DownloadUrl))
                return shareLinks[0].DownloadUrl;

            throw new PanelApiException("获取直链失败");
        }

        /// <summary>
        /// 夸克下载上下文。
        ///
        /// ⚠️ 必须下发**客户端 UA**（<c>QuarkConstants.DlUa</c>）而不是浏览器 UA：
        /// 夸克 CDN 会校验「取链时声明的身份」与「下载时的身份」是否同族，
        /// 混用容易被判为盗链（见 QuarkConstants.DlUa 的详细说明）。
        /// </summary>
        DownloadRequestContext IPanelClient.BuildDownloadContext()
        {
            return new DownloadRequestContext
            {
                Cookie = CookieStr,   // 走原客户端已有的公开属性，转调 _session.BuildCookieString()
                UserAgent = Support.QuarkConstants.DlUa,
                Referer = Support.QuarkConstants.Referer,
                SuggestedThreads = 0,   // 0 = 用引擎默认（夸克不限速，无需降级）
                SpeedNote = "",
            };
        }
    }
}
