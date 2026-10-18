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

namespace ZERA.Core.Api
{
    /// <summary>
    /// 百度网盘网页端接口常量与错误码。
    ///
    /// 【⚠️ 全部为非公开接口】百度没有对外开放网盘下载 API，
    /// 这里用的是网页版自己调的接口，随时可能变更。与夸克同一性质，
    /// 因此本程序同样不承诺长期可用。
    /// </summary>
    public static class BaiduConstants
    {
        public const string ApiBase = "https://pan.baidu.com";
        public const string Referer = "https://pan.baidu.com/";
        public const string Origin = "https://pan.baidu.com";

        /// <summary>登录态 Cookie 名。百度的一切鉴权都靠它，缺失即视为未登录。</summary>
        public const string LoginCookieKey = "BDUSS";

        /// <summary>CSRF 令牌 Cookie。调用接口时通常要同时带上 BDCLND / STOKEN。</summary>
        public const string CsrfCookieKey = "BDCLND";

        /// <summary>分享页地址。解析分享时先 GET 它，从 HTML 里抠出 sign / timestamp / shareid。</summary>
        public const string SharePage = ApiBase + "/s/";

        /// <summary>提交提取码换 sekey（即 BDCLND Cookie）。</summary>
        public const string ShareVerify = ApiBase + "/share/verify";

        /// <summary>分享文件列表（需要 sekey 作为查询参数）。</summary>
        public const string ShareList = ApiBase + "/share/list";

        /// <summary>取直链。必须 POST，GET 无效。</summary>
        public const string Sharedownload = ApiBase + "/api/sharedownload";

        /// <summary>
        /// 网页端「下载自己网盘文件」的标准接口（POST，form 里 product=pc + fid_list）。
        ///
        /// <para>与 <see cref="Sharedownload"/> 的区别：后者是**分享**语义（要 primaryid），
        /// 用在自己网盘文件上属于碰运气。这个才是网页版「下载」按钮真正调用的接口，
        /// 在 locatedownload / xpan 都返回空时是最后一条正经出路。</para>
        /// </summary>
        public const string WebDownload = ApiBase + "/api/download";

        /// <summary>"我的网盘"文件列表（网页端接口，dir 传**路径**，根目录是 "/"）。</summary>
        public const string FileList = ApiBase + "/api/list";

        /// <summary>
        /// 分享转存（"保存到网盘"）。网页端「保存」按钮打的就是这个接口：
        /// query 带 shareid/from(uk)/sekey/bdstoken/logid，form 带 fsidlist/path。
        /// 用于分享直链被百度拒发（风控/大小限制）时的兜底路径 ——
        /// 转存到自己的网盘后再按 locatedownload 取链（走原生客户端身份）。
        /// 抓包依据：bwmelon.com/archives/45 等多方一致，errno 0=成功 4=已存在。
        /// </summary>
        public const string ShareTransfer = ApiBase + "/share/transfer";

        /// <summary>创建目录（isdir=1）。转存前确保目标目录存在。</summary>
        public const string DirCreate = ApiBase + "/api/create";

        /// <summary>
        /// PCS 接口域 —— 官方**原生客户端**协议栈（与网页端 pan.baidu.com 是两套身份）。
        /// BaiduPCS-Go 等开源实现沿用多年，取链走这里 + netdisk UA + app_id 266719，
        /// 服务端会把它分进"客户端带宽池"（比网页端池宽松，非会员侧唯一加速杠杆）。
        /// </summary>
        public const string PcsBase = "https://pcs.baidu.com";

        /// <summary>PCS 文件接口。method=locatedownload 是官方客户端的取链方式。</summary>
        public const string PcsFileApi = PcsBase + "/rest/2.0/pcs/file";

        /// <summary>
        /// 原生客户端协议栈的 app_id（BaiduPCS-Go 默认值，与网页端 250528 区分）。
        /// ⚠️ 这两个值都是长期在役的默认身份，**不要随意改** —— 社区实测改动会触发风控。
        /// </summary>
        public const string NativeAppId = "266719";

        /// <summary>网页端新列目录/下载接口（xpan）。/api/list 的现代替代，同样按路径寻址。</summary>
        public const string XpanFileApi = ApiBase + "/rest/2.0/xpan/file";

        /// <summary>取 bdstoken（接口的 CSRF 参数）。</summary>
        public const string TokenApi = ApiBase + "/api/gettemplatevariable?fields=%5B%22bdstoken%22%5D";

        /// <summary>账号信息（含昵称与会员状态）。</summary>
        public const string UserInfoApi = ApiBase + "/api/loginInfo?clienttype=0&app_id=250528&web=1";
        public const string MembershipApi = ApiBase + "/rest/2.0/membership/user?app_id=250528&web=5&method=query";

        /// <summary>容量信息。</summary>
        public const string QuotaApi = ApiBase + "/api/quota?checkfree=1&clienttype=0&app_id=250528&web=1";

        /// <summary>
        /// 验证 Cookie 是否有效并取账号信息的轻接口。
        /// 比 loginInfo 更轻，且未登录时返回结构也不同（便于区分）。
        /// </summary>
        public const string CheckLoginApi = ApiBase + "/api/gettemplatevariable?fields=%5B%22username%22%5D";

        // ---------------- 扫码登录（2026-10-14 实测确认可用） ----------------
        // 三段式：getqrcode 开会话 → channel/unicast 长轮询 → qrbdusslogin 换 BDUSS。
        // 与夸克的最大差异：passport.baidu.com 在 .NET SChannel 下可达，
        // **不需要** Python 桥绕 TLS 指纹（详见 BaiduQrLoginService 的类注释）。

        /// <summary>扫码登录域（百度通行证，与网盘域 pan.baidu.com 不同）。</summary>
        public const string PassportBase = "https://passport.baidu.com";

        /// <summary>第一步：创建扫码会话，返回 imgurl（二维码图）与 sign（会话 ID）。</summary>
        public const string QrGetCode = PassportBase + "/v2/api/getqrcode?lp=pc";

        /// <summary>第二步：二维码图片地址（把 <c>sign</c> 拼上去即可，直接返回 PNG）。</summary>
        public const string QrImage = PassportBase + "/v2/api/qrcode";

        /// <summary>
        /// 第三步：长轮询会话状态。
        /// <para>服务端会 hold 住连接约 30 秒再返回当前状态（未扫码时返回
        /// <c>{"errno":1}</c>）；用户一旦扫码/确认则**立即**返回。</para>
        /// </summary>
        public const string QrUnicast = PassportBase + "/channel/unicast";

        /// <summary>第四步：用轮询拿到的临时 bduss 兑换正式登录态（Set-Cookie 带 BDUSS）。</summary>
        public const string QrBdussLogin = PassportBase + "/v3/login/main/qrbdusslogin";

        /// <summary>unicast 轮询：还没人扫码。</summary>
        public const int QrErrWaitingScan = 1;

        /// <summary>unicast 轮询：有结果了（channel_v 里带 status / 临时 bduss）。</summary>
        public const int QrErrHasResult = 0;

        /// <summary>
        /// 轮询单次请求的客户端超时（秒）。
        /// 必须大于服务端的 30 秒 hold 时间，否则会在服务端返回前被我们自己掐断。
        /// </summary>
        public const int QrPollTimeoutSeconds = 45;

        /// <summary>整个扫码会话的有效期（秒）。超时后让用户重新取码。</summary>
        public const int QrSessionTimeoutSeconds = 300;

        // ---------------- app_id / web ----------------
        // 这些参数在百度网页端是固定的，少一个就可能被判为非法请求。

        public const string AppId = "250528";
        public const string Web = "1";
        public const string ClientType = "0";
        public const string Channel = "chunlei";

        // ---------------- 业务错误码 ----------------
        // 取自 baidu-wangpan-parse 等开源实现与实测记录。

        /// <summary>成功。</summary>
        public const int ErrOk = 0;

        /// <summary>内容包含违规信息。</summary>
        public const int ErrViolation = -1;

        /// <summary>
        /// 需要图形验证码。
        /// ⚠️ 这是接入百度最麻烦的一点：触发后**不能自动重试** ——
        ///    继续重试只会让风控冷却时间越滚越长，必须停下来让用户手工过码。
        /// </summary>
        public const int ErrNeedCaptcha = -20;

        /// <summary>通用失败。</summary>
        public const int ErrDownloadFail = 2;

        /// <summary>分享已过期（页面失效，需重新拿链接）。</summary>
        public const int ErrShareExpired = 113;

        /// <summary>分享不存在 / 已取消。</summary>
        public const int ErrShareNotFound = 116;

        /// <summary>没有下载权限（常见于分享者禁止下载，或需要密码）。</summary>
        public const int ErrNoPermission = 118;

        /// <summary>一次操作的文件过多。</summary>
        public const int ErrTooManyFiles = 121;

        /// <summary>
        /// 文件夹打包超过服务端上限（300MB）。
        /// 百度把整个文件夹打成 zip 下载有硬上限，超了就报这个。
        /// → **本程序的做法是只列单文件、逐个下，从根本上避开这个限制。**
        /// </summary>
        public const int ErrPackageTooLarge = 31090;

        /// <summary>提取码错误。</summary>
        public const int ErrBadPasscode = -9;

        /// <summary>登录态失效（Cookie 过期 / 被挤下线）。</summary>
        public const int ErrNotLogin = -6;

        // ---------------- 限制与默认值 ----------------

        /// <summary>
        /// 非会员的下载连接数。
        ///
        /// 【2026-10-14 晚重做：4 → 2】原来的 4 线程是"看着给"的值，没有依据。
        /// 社区实测（BaiduPCS-Go 文档与其排障表）给出的证据完全相反：
        /// **普通账号的并发耐受度极低** —— max_parallel / max_download_load 大于 1
        /// 就可能触发惩罚性降速，且惩罚持续数小时甚至数天，实际速度反而比单线程更低。
        /// 百度非会员的配额是**按账号**下的令牌桶（不是按连接），多连接本来就不会
        /// 更快，却会把账号拖进风控名单 —— 这是自己给自己降速。
        ///
        /// 取 2 而不是文档建议的 1：单连接在百度侧容易被吊住（连接被 hold、
        /// 偶发重置），而本引擎有单连接停滞收割（90s 无字节即掐断重排），
        /// 双连接足够兜底；2 仍远低于触发惩罚的高并发区间。
        /// 若实测仍出现惩罚性降速，把这个值改成 1 即可。
        /// </summary>
        public const int FreeThreads = 2;

        /// <summary>会员线程数。实测会员可开到数十。</summary>
        public const int VipThreads = 32;

        /// <summary>
        /// 文件夹打包下载的服务端上限（字节）。
        /// 用于在 UI 上提前给出提示，避免用户等半天最后拿到 31090。
        /// </summary>
        public const long PackageSizeLimit = 300L * 1024 * 1024;

        /// <summary>单次取链的最大文件数。防止超大文件夹把界面卡住。</summary>
        public const int MaxResolveItems = 2000;

        /// <summary>
        /// 下载直链用的**客户端 UA** —— 这是非会员侧唯一可用的加速杠杆。
        ///
        /// <para>【原理】百度服务端按 UA 把请求分进不同带宽池：浏览器 UA 进"网页端池"
        /// （非会员约 100–300 KB/s），<c>netdisk;</c> 前缀的客户端 UA 进"客户端池"
        /// （分配策略更宽松，且支持多连接）。社区生态（IDM / Motrix / aria2 + 网页端
        /// 解析脚本）普遍采用"网页端取链 + 客户端 UA 下载"的组合，2024–2025 年一直有效
        /// （见 github.com/dasan5616/baidupan 等方法汇总）。</para>
        ///
        /// <para>【UA 格式依据】官方 PC 客户端 UA 的格式为
        /// <c>netdisk;&lt;版本&gt;;PC;PC-Windows;&lt;系统 build&gt;;WindowsBaiduYunGuanJia</c>，
        /// BaiduPCS-Go 等开源工具沿用了十几年（其默认值 <c>netdisk;5.2.7;PC;PC-Windows;6.2.9200;…</c>）。
        /// 服务端分类器认的是 <c>netdisk;</c> 前缀 + <c>PC</c> 平台标记，版本号取当前
        /// 主流客户端版本（7.60.x，2025 年在役）即可。</para>
        ///
        /// <para>【保险】若百度哪天严格校验"取链 UA 与下载 UA 同族"导致 403，
        /// 引擎会自动回退到网页端 UA 重试（见 <see cref="DownloadRequestContext.FallbackUserAgent"/>
        /// 与 SegmentedDownloader 的主循环）——已下载的分片进度保留，不重下。</para>
        /// </summary>
        public const string DownloadUa =
            "netdisk;7.60.5.102;PC;PC-Windows;10.0.19045;WindowsBaiduYunGuanJia";

        public const string DefaultFolder = "ZERA";
    }
}
