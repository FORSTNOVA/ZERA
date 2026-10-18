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

namespace ZERA.Core.Models
{
    /// <summary>取直链接口返回的单条记录（drive 与分享下载共用）。</summary>
    public class DownloadLink
    {
        public string Fid { get; set; }
        public string DownloadUrl { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }

        /// <summary>
        /// 服务端下发的账号限速值（字节/秒）。0 = 响应没带/未知。
        /// 目前只有百度原生 locatedownload 路径会填（sl 字段）；如实展示用，
        /// 不用于限速客户端 —— 服务端的配额客户端绕不过，如实说明即可。
        /// </summary>
        public long SpeedLimitBps { get; set; }

        /// <summary>
        /// 【2026-10-14 晚】取出这条直链的账号键（多账号轮询时有值）。
        /// 百度直链与取链账号的 Cookie 绑定，下载必须用同一账号的会话 ——
        /// 所以取链时是哪个账号，下载与刷新时就必须还是那个账号。
        /// </summary>
        public string AccountKey { get; set; }
    }
}
