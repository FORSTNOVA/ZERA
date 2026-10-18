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
using System.IO;
using System.Runtime.InteropServices;

namespace ZERA
{
    public static class Util
    {
        public static string FormatSize(long n)
        {
            // 🚨 【2026-10-06 修】原来除以 1024 却标 "KB/MB/GB"（那是 KiB/MiB/GiB）。
            // 后果：同一个文件「夸克分享页写 9.54 GB、本程序写 8.89 GB」，用户以为少下了。
            // 改成 **1000 进制** —— 单位名副其实，也顺带让速度显示符合网络速率的惯例（1 MB/s = 10^6 B/s）。
            double v = n;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < units.Length - 1)
            {
                v /= 1000.0;
                i++;
            }
            return i == 0 ? n + " B" : v.ToString("F2") + " " + units[i];
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out string ppszPath);

        /// <summary>取 Windows「下载」文件夹的真实路径（可能被重定向到别的盘/OneDrive），与原版 _downloads_dir 一致。</summary>
        public static string GetDownloadsFolder()
        {
            try
            {
                // FOLDERID_Downloads
                var fid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                int hres = SHGetKnownFolderPath(ref fid, 0, IntPtr.Zero, out string path);
                if (hres == 0 && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>尝试创建目录并写入一个测试文件，判断该目录是否真的可写。</summary>
        public static bool IsWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;
            try
            {
                Directory.CreateDirectory(dir);
                string test = Path.Combine(dir, ".w_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(test, "1");
                File.Delete(test);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>应用本地数据目录（%LOCALAPPDATA%，系统保证可写、不受「受控文件夹访问」保护）。</summary>
        public static string GetLocalAppDataDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZERA");
        }

        /// <summary>
        /// 【2026-10-14】把旧数据文件搬过来。
        ///
        /// 产品经历过两次改名：鸽子下载 / GeZi → ZEKR → ZERA，本地数据文件名一路跟着变
        /// （<c>GeZi.config.xml</c> / <c>GeZi.state.json</c> → ZEKR 前缀 → ZERA 前缀）。
        /// 若不迁移，老用户升级后会**丢掉全部设置**（含登录态、下载目录、主题）
        /// 与历史记录、未完成任务列表 —— 那是一次毫无必要的"重新配一遍"。
        ///
        /// 策略：新文件不存在、旧文件存在时**复制**（不是移动）过去。
        /// 用复制而不是移动，是为了万一新版有问题，用户删掉新文件就能立刻回到旧版状态。
        /// 任何一步失败都静默继续：迁移只是体验优化，不该让程序起不来。
        /// </summary>
        public static void MigrateLegacyDataFile(string legacyPath, string newPath)
        {
            if (string.IsNullOrEmpty(legacyPath) || string.IsNullOrEmpty(newPath))
                return;
            try
            {
                if (File.Exists(newPath) || !File.Exists(legacyPath))
                    return;
                var dir = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(legacyPath, newPath, overwrite: false);
            }
            catch { }
        }
    }
}
