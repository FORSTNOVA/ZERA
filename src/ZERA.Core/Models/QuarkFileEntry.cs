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
    /// <summary>夸克目录/文件条目，经过 _norm 归一化后的形态。</summary>
    public class QuarkFileEntry
    {
        public string Fid { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public bool IsDir { get; set; }
        /// <summary>分享场景下的 share_fid_token，取直链时需要。</summary>
        public string Token { get; set; }

        /// <summary>
        /// 平台侧的文件路径（**百度专用**）。
        /// 百度的列目录与取链接口都按路径寻址而不是 fid，所以界面导航键用这个值；
        /// 夸克按 fid 寻址，此字段为 null（界面回退用 Fid）。
        /// </summary>
        public string Path { get; set; }
    }
}
