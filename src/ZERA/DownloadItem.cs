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

namespace ZERA
{
    /// <summary>取直链后的下载条目（名字/大小/直链/相对目录），与分享原 fid 解耦。</summary>
    internal class DownloadItem
    {
        public string Name;
        public long Size;
        public string Url;
        public string RelDir;

        /// <summary>文件在网盘/分享里的 fid。直链过期时需要它去重新取链。</summary>
        public string Fid;

        /// <summary>分享内下载用的 share_fid_token（免转存路径需要）。</summary>
        public string Token;

        /// <summary>true = 走「分享免转存」取链（需要 pwd_id + stoken + token），false = 走我的网盘取链。</summary>
        public bool FromShare;

        /// <summary>
        /// 【2026-10-14 多平台】这个文件来自哪个网盘。
        ///
        /// <para><b>为什么必须持久化：</b>直链过期时下载器会回调刷新，
        /// 而刷新必须知道"该找哪个平台的客户端"。用户可以在设置里切换当前平台，
        /// 也可以在夸克登录着的同时添加一个百度任务 —— 若不记录来源平台，
        /// 重启后续传就会用错客户端，轻则取不到链，重则取到同名的错误文件。</para>
        ///
        /// <para>缺省值是 <see cref="PanelKind.Quark"/>（0），因此老版本持久化的
        /// 数据反序列化后天然仍指向夸克，无需迁移。</para>
        /// </summary>
        public Core.Panels.PanelKind Panel = Core.Panels.PanelKind.Quark;

        /// <summary>
        /// 【2026-10-14 多账号轮询】这个文件的直链是哪个百度账号取到的。
        ///
        /// <para><b>为什么必须记录：</b>百度直链与取链账号的登录态绑定，
        /// 下载和过期刷新都必须用同一账号的 Cookie 会话，换账号会被服务端拒绝。
        /// 空串 = 当前会话账号（单账号下载或轮询回退兜底的情况）。</para>
        ///
        /// <para>夸克文件此字段无意义（夸克不按账号绑定直链），恒为空。</para>
        /// </summary>
        public string AccountKey;
    }
}

