// ZERA —— 夸克网盘下载器 (Quark netdisk downloader)
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
using System.Threading;
using System.Threading.Tasks;

namespace ZERA.Core.Download
{
    /// <summary>一个下载任务：URL + 目标路径 + 独立取消/暂停令牌。</summary>
    public class DownloadJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Cookie { get; set; } = "";
        public string Authorization { get; set; }
        public string CredentialScope { get; set; }

        /// <summary>
        /// 下载请求用的 UA / Referer / Origin。
        /// 【2026-10-14 晚】以前这三个值在引擎里硬编码成夸克的 —— 百度下载
        /// 因此一直带着夸克客户端 UA 和 quark.cn 的 Referer/Origin 去请求
        /// 百度 CDN（潜在 403/风控特征）。现在由各平台的下行上下文
        /// （<see cref="ZERA.Core.Panels.IPanelClient.BuildDownloadContext"/>）显式下发。
        /// 留空 = 用引擎默认（夸克的值），老调用方行为不变。
        /// </summary>
        public string UserAgent { get; set; }
        public string Referer { get; set; }
        public string Origin { get; set; }

        /// <summary>
        /// 下载 UA 被服务端拒绝（401/403/412）时的回退 UA / Referer / Origin。
        /// 引擎会自动换这套重试（保留分片进度）。留空 = 无回退。
        /// 百度：主 UA 是客户端 UA（带宽池更优），回退是网页端 UA（保底）。
        /// </summary>
        public string FallbackUserAgent { get; set; }
        public string FallbackReferer { get; set; }
        public string FallbackOrigin { get; set; }

        /// <summary>0 表示用默认线程数。</summary>
        public int Threads { get; set; } = 0;

        public long MaxRequestBytes { get; set; }

        /// <summary>
        /// 分片数据的落盘方式。默认直写同一文件（峰值 1×、速度与另一种相当）；
        /// 需要"边下边播"时改为 SegmentedFiles。
        /// </summary>
        public WriteMode WriteMode { get; set; } = WriteMode.SharedFile;

        /// <summary>
        /// 分片/续传元数据的根目录覆盖。null/空 = 与目标文件同级（默认）。
        /// 见 SegmentedDownloader 的 _partsRoot 说明。
        /// </summary>
        public string PartsRoot { get; set; }

        /// <summary>
        /// 重新获取本文件直链的回调。夸克直链带时效签名，长任务下到一半会过期；
        /// 提供它之后，下载器遇到 401/403/412 能自动换新链续传，而不是整个任务失败。
        /// 由 UI 层注入 —— 只有它知道这个文件对应的 fid / token / 分享上下文。
        /// </summary>
        public Func<CancellationToken, Task<string>> LinkRefresher { get; set; }

        /// <summary>
        /// 【免转存专用】为 true 时，失败文案**不再承诺"可续传"**。
        /// 免转存的续传取链依赖分享 stoken，而续传**不会重新换 stoken**（只在解析分享时取一次），
        /// stoken 过期就永远取不到新链 → 提示必须改成"需重新解析分享"。
        /// 由 UI 层按 <c>Pending.FromShare</c> 设置（FromShare == true ⟺ 免转存）。
        /// </summary>
        public bool ResumeCannotRefresh { get; set; }

        /// <summary>
        /// 续传上下文（可选）。由 UI 层在创建任务时填好，用于把该任务写进
        /// 「未完成任务」持久化记录，使程序重启后能**不依赖界面上下文**地重取直链续传。
        ///
        /// 为什么放在 Job 上而不是让 UI 自己拼：UI 在写 pending 时只有 <c>TaskItem</c>，
        /// 而这些字段（fid/token/分享上下文/相对目录）在下载链路里就只存在于创建那一刻，
        /// 挂在 Job 上最不容易漏传。
        /// </summary>
        public PendingInfo Pending { get; set; }

        public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();
        public PauseToken Pause { get; } = new PauseToken();

        public void CancelJob() { try { Cancel.Cancel(); } catch { } }
        public void PauseJob() => Pause.Pause();
        public void ResumeJob() => Pause.Resume();
    }

    /// <summary>
    /// 一个下载任务的「续传上下文」—— 让任务能在程序重启后被重新取链并续下。
    ///
    /// <para>
    /// 除 <see cref="Dest"/> 外的字段都用于**重新申请直链**（旧链带时效签名，必已失效）。
    /// 分享模式下缺 <see cref="Token"/> / <see cref="PwdId"/> / <see cref="Stoken"/>
    /// 就无法重取（例如免转存/游客直链任务），此时 <see cref="CanResume"/> 为 false。
    /// </para>
    /// </summary>
    public class PendingInfo
    {
        /// <summary>
        /// 【2026-10-14 多平台】这个待续传任务属于哪个网盘。
        ///
        /// <para><b>为什么必须有：</b>程序重启后续传要重建直链，而取链必须走
        /// **原平台**的客户端。若用户中途把当前平台从夸克切到百度，没有这个字段
        /// 就会拿百度客户端去取夸克的 fid，轻则失败，重则取到同名但完全不同的文件
        /// （静默下载错内容 —— 这是最坏的一类 bug）。</para>
        ///
        /// <para>缺省 <see cref="Panels.PanelKind.Quark"/>（0），老数据反序列化后
        /// 天然仍指向夸克，**无需迁移**。</para>
        /// </summary>
        public Panels.PanelKind Panel { get; set; } = Panels.PanelKind.Quark;

        /// <summary>
        /// 【2026-10-14 晚】取出这条直链的账号键（百度多账号轮询时有值）。
        /// 百度直链与取链账号的登录态绑定，续传/刷新取链必须用同一账号；
        /// 空 = 用当前平台的默认账号。夸克始终为空。
        /// </summary>
        public string AccountKey { get; set; }

        /// <summary>分享的 pwd_id（分享模式才有）。</summary>
        public string PwdId { get; set; }
        /// <summary>分享的 stoken（分享模式才有）。</summary>
        public string Stoken { get; set; }
        /// <summary>提取码；无码为空。</summary>
        public string Passcode { get; set; }
        /// <summary>网盘文件 fid（所有模式都有）。</summary>
        public string Fid { get; set; }
        /// <summary>分享内文件的 share fid token（分享模式才有）。</summary>
        public string Token { get; set; }
        /// <summary>是否来自分享（否则为"我的网盘"）。</summary>
        public bool FromShare { get; set; }
        /// <summary>相对目录（含尾部分隔符时也算合法），用于重建目标路径。</summary>
        public string RelDir { get; set; }
        /// <summary>目标文件完整路径。</summary>
        public string Dest { get; set; }
        /// <summary>文件总大小（0 = 未知）。</summary>
        public long Size { get; set; }

        /// <summary>是否具备重取链所需的全部信息。</summary>
        public bool CanResume
        {
            get
            {
                if (string.IsNullOrEmpty(Dest) || string.IsNullOrEmpty(Fid)) return false;
                if (!FromShare) return true;                       // 我的网盘：只需 fid
                if (Panel == Panels.PanelKind.Baidu)
                {
                    // 百度分享任务的续传能力弱于夸克：取链需要分享页实时解析出的
                    // sign/timestamp/shareid，这些**无法持久化**（每次都变）。
                    // 所以百度分享任务一律判为"不可自动续传"，由界面提示用户重新解析。
                    // 这是平台机制决定的，不是实现取舍 —— 见 BaiduClient.RefreshLinkAsync。
                    return false;
                }
                return !string.IsNullOrEmpty(Token)
                    && !string.IsNullOrEmpty(PwdId)
                    && !string.IsNullOrEmpty(Stoken);
            }
        }
    }
}
