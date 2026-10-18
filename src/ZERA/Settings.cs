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
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using ZERA.Core.Support;
using ZERA.Core.Panels;

namespace ZERA
{
    /// <summary>
    /// 【2026-10-14 多平台】单条「平台 → Cookie」记录。
    ///
    /// 必须是一个可序列化的简单类型：<see cref="XmlSerializer"/> 不支持
    /// Dictionary（见 <see cref="AppSettings.PanelCookies"/> 的说明）。
    /// </summary>
    public class PanelCookieEntry
    {
        public string AccountKey { get; set; } = "";
        /// <summary>平台。缺省 0 = 夸克，与老配置一致。</summary>
        public ZERA.Core.Panels.PanelKind Panel { get; set; }

        /// <summary>该平台的 Cookie（落盘前经 DPAPI 加密）。</summary>
        public string Cookie { get; set; } = "";
    }
    public class AppSettings
    {
        /// <summary>
        /// 单任务下载线程数（= 单文件最大并发连接）。默认 64，与 Python Flet 版一致
        /// （core 的 DOWNLOAD_THREADS 也是 64）。上限见 SegmentedDownloader.MaxRealConcurrency。
        /// </summary>
        public int Threads { get; set; } = 64;

        public int ConcurrentTasks { get; set; } = 3;
        public string OutDir { get; set; } = "";
        public bool AutoClean { get; set; } = true;

        /// <summary>
        /// 分片落盘方式。SharedFile = 直写同一文件（默认，峰值 1×）；
        /// SegmentedFiles = 独立分片 + 边合并（可边下边播，峰值 1×+单片）。
        /// 老配置文件里没有这个字段，反序列化后保持默认值 SharedFile，行为与旧版一致。
        /// </summary>
        public ZERA.Core.Download.WriteMode WriteMode { get; set; } = ZERA.Core.Download.WriteMode.SharedFile;

        /// <summary>
        /// 分片/续传元数据的根目录。留空 = 与目标文件同级（`{目标}.qparts`），即原有行为。
        /// 移植自 Python Flet 版的 part_dir：可把分片集中到另一块盘（如 SSD）。
        /// </summary>
        public string PartsRoot { get; set; } = "";

        /// <summary>
        /// 是否把分片目录设为「隐藏」。移植自 Python Flet 版的 hide_parts。
        /// 只影响新建的分片目录在资源管理器里是否显示（不影响功能）。
        /// </summary>
        public bool HideParts { get; set; } = true;

        /// <summary>
        /// 曾经用过的分片根目录清单（最多 <see cref="PartsDirsMax"/> 个，最近使用的在末尾）。
        ///
        /// 移植自 Python Flet 版的 `part_dirs_known`。存在的理由很实际：
        /// 用户改过「默认分片位置」之后，**旧目录里的残留分片既扫不到、也没入口去清**，
        /// 只能一直烂在磁盘上（"分片根本没删掉"就是这么来的）。
        /// 记住历史目录后，清理/统计时可以把这些位置的残留一并处理。
        /// </summary>
        public List<string> PartsDirsKnown { get; set; } = new List<string>();

        /// <summary>历史分片目录清单的长度上限（与 Python 版一致）。</summary>
        public const int PartsDirsMax = 20;

        /// <summary>
        /// 登录 Cookie。内存里始终是明文（供 QuarkClient 使用），
        /// 序列化到磁盘时由 Save/Load 做 DPAPI 加解密，字段本身不感知。
        /// </summary>
        public string Cookie { get; set; } = "";

        /// <summary>
        /// 【2026-10-14 多平台】当前选中的网盘。
        ///
        /// <para><b>为什么要按平台各存一份 Cookie：</b>夸克与百度的登录态互不相通，
        /// 而且用户很可能同时用两个网盘。用一份 Cookie 字段意味着切换平台就得
        /// 重新登录一次。</para>
        ///
        /// <para>老配置里没有这个节点 → XmlSerializer 给默认值 0 = <c>PanelKind.Quark</c>
        /// → 升级后默认仍是夸克，行为与旧版一致。</para>
        /// </summary>
        public ZERA.Core.Panels.PanelKind Panel { get; set; } = ZERA.Core.Panels.PanelKind.Quark;

        /// <summary>
        /// 【2026-10-14 晚】百度分享下载是否启用**多账号轮询**。
        ///
        /// 百度的限速是按账号下发的配额。分享链接任意登录账号都能下，
        /// 于是把一批文件分摊到多个账号各自取链+下载，总吞吐 ≈ 各账号配额之和
        /// —— 这是不碰会员、不破解的前提下唯一能线性提升吞吐的合法手段。
        /// 只对**分享**生效：自己网盘的文件每个账号须 genuinely 拥有才下得了。
        /// 老配置没有此字段 → 默认 true（有多个账号时自动生效）。
        /// </summary>
        public bool MultiAccountDownload { get; set; } = true;

        /// <summary>
        /// 【2026-10-14 多平台】各平台的 Cookie。
        ///
        /// <para><b>为什么用 <see cref="List{PanelCookieEntry}"/> 而不是 Dictionary：</b>
        /// <see cref="XmlSerializer"/> **不支持序列化实现了 IDictionary 的成员** ——
        /// 实测直接抛 <c>NotSupportedException: 无法序列化 …Dictionary`2 的成员
        /// AppSettings.PanelCookies，因为它实现 IDictionary</c>，
        /// 而且是在 <c>XmlSerializer</c> 的**静态构造**里抛的，
        /// 于是 <c>SettingsStore</c> 的类型初始化失败 → 整个程序起不来（实测确实白屏了）。
        /// 换成 List 后立刻正常。</para>
        ///
        /// <para>落盘时同样走 DPAPI 加密 —— 里面就是用户的 BDUSS，
        /// 和原来的夸克 Cookie 敏感级别一致。见 Save/Load 里的处理。</para>
        /// </summary>
        public List<PanelCookieEntry> PanelCookies { get; set; } = new List<PanelCookieEntry>();

        /// <summary>
        /// 取某平台的 Cookie；没有记录返回空串。
        /// </summary>
        public string GetPanelCookie(PanelKind kind)
        {
            if (PanelCookies == null) return "";
            foreach (var e in PanelCookies)
                if (e.Panel == kind) return e.Cookie ?? "";
            return "";
        }

        /// <summary>写入某平台的 Cookie（不存在则新增）。</summary>
        public void SetPanelCookie(PanelKind kind, string cookie)
        {
            if (PanelCookies == null) PanelCookies = new List<PanelCookieEntry>();
            foreach (var e in PanelCookies)
            {
                if (e.Panel == kind) { e.Cookie = cookie ?? ""; return; }
            }
            PanelCookies.Add(new PanelCookieEntry { Panel = kind, Cookie = cookie ?? "" });
        }

        public string Who { get; set; } = "";

        /// <summary>
        /// 账号头像 URL（登录时从 account/info 探测到的）。
        /// 留空 = 拿不到 → 界面回退到"昵称首字"头像。
        /// 头像链接可能带时效，每次登录/启动校验都会重新探测覆盖。
        /// </summary>
        public string AvatarUrl { get; set; } = "";

        /// <summary>
        /// **当前登录账号的档案 Key**（对应 accounts/{key}.xml 的文件名）。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用来判断"账号列表里哪一行是当前账号"。
        /// 原来是用**整串 Cookie 做字符串比较**（`acc.Cookie == _client.CookieStr`），
        /// 但运行时 Cookie 会被重建/刷新（顺序、条目都可能变），所以永远比不中 ——
        /// 表现为「正在登录的那个账号，它的『切换』按钮居然还是可点的」（用户反馈）。
        /// 存 Key 就没有这个问题：Key 是夸克返回的 uid，稳定不变。
        /// </remarks>
        public string CurrentAccountKey { get; set; } = "";

        public string GetAccountKey(PanelKind panel)
            => PanelCookies?.Find(e => e.Panel == panel)?.AccountKey ?? "";

        public void RememberAccount(string key)
        {
            SetPanelCookie(Panel, GetPanelCookie(Panel));
            PanelCookies.Find(e => e.Panel == Panel).AccountKey = key ?? "";
            CurrentAccountKey = key ?? "";
        }

        /// <summary>
        /// 点主窗口「关闭」按钮时的行为。见 <see cref="CloseAction"/>。
        /// </summary>
        /// <remarks>
        /// 【2026-10-03 新增】用户要求：「第一次时会询问用户，然后可以选择最小化托盘
        /// 或者是直接关闭程序，并且在这个弹窗的右下角可以勾选此后不再提示，在设置中可以修改」。
        /// ⚠️ 默认值必须是 0 = <see cref="CloseAction.Ask"/>：
        ///    XmlSerializer 反序列化**不会执行字段初始化器**，老配置文件里没有这个节点时
        ///    属性会保持 default(T) = 0 —— 正好就是"第一次要问"。
        /// </remarks>
        public CloseAction OnCloseButton { get; set; } = CloseAction.Ask;

        /// <summary>
        /// 小窗左上角坐标。⚠️ 必须配 <see cref="MiniPosSet"/> 用：
        /// XmlSerializer 反序列化不跑字段初始化器，老配置里缺这个节点时值是 0
        /// （而不是 NaN），光看 0 分不清"没摆过"还是"就摆在 (0,0)"。
        /// </summary>
        public double MiniLeft { get; set; }
        public double MiniTop { get; set; }

        /// <summary>小窗是否已经摆过位置（false = 首次打开，贴屏幕右下角）。</summary>
        public bool MiniPosSet { get; set; }

        /// <summary>
        /// 外观主题（2026-10-04 新增）。Light / Dark / System。
        ///
        /// ⚠️ 默认值必须是 0 = <see cref="AppTheme.System"/>：
        ///    XmlSerializer 反序列化**不执行字段初始化器**，老配置里没有这个节点时
        ///    属性保持 default(T) = 0。这里刻意让 0 对应 System（而不是 Light），
        ///    于是**老用户升级后自动跟随系统** —— 如果系统是浅色，观感与升级前完全一致；
        ///    系统是深色则界面跟着变深，正是"跟随系统"该有的行为。
        /// </summary>
        public AppTheme Theme { get; set; } = AppTheme.System;
    }

    /// <summary>主窗口「关闭」按钮的行为。</summary>
    public enum CloseAction
    {
        /// <summary>弹一次询问框（默认）。用户勾了「此后不再提示」后会被改成下面两个之一。</summary>
        Ask = 0,
        /// <summary>隐藏到托盘，下载继续跑。</summary>
        MinimizeToTray = 1,
        /// <summary>直接退出程序。</summary>
        Exit = 2,
    }

    public static class SettingsStore
    {
        private static readonly XmlSerializer Ser = new XmlSerializer(typeof(AppSettings));

        private static string _filePath;
        private static bool _loaded;

        /// <summary>
        /// 配置文件路径：优先 exe 同目录（绿色便携，数据随 U 盘走）；
        /// 同目录不可写（装在 Program Files、"受控文件夹访问" 拦截等）时
        /// 回退到 %LOCALAPPDATA%\ZERA\，保证任何情况下都能存住设置。
        /// </summary>
        public static string FilePath
        {
            get
            {
                if (_loaded && !string.IsNullOrEmpty(_filePath))
                    return _filePath;

                _filePath = ResolveFilePath();
                _loaded = true;
                return _filePath;
            }
        }

        private static string ResolveFilePath()
        {
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZERA.config.xml");

            // exe 同目录已经有一份配置文件 → 无条件沿用（避免老用户设置"搬家"到别处）
            try
            {
                if (File.Exists(portable))
                    return portable;
            }
            catch { }

            // 【2026-10-14】改名迁移链：GeZi.config.xml → ZEKR.config.xml → ZERA.config.xml。
            // exe 同目录还留着任一旧名的配置 → 按"新→旧"顺序搬过来再沿用
            // （MigrateLegacyDataFile 内部是"目标已存在就跳过"，所以排在前面的旧名优先）。
            foreach (string legacyName in LegacyConfigNames)
            {
                Util.MigrateLegacyDataFile(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, legacyName), portable);
            }
            try
            {
                if (File.Exists(portable))
                    return portable;
            }
            catch { }

            // 试着真的在 exe 同目录落一个文件，确认可写
            if (Util.IsWritable(AppDomain.CurrentDomain.BaseDirectory))
                return portable;

            string fallbackDir = Util.GetLocalAppDataDir();
            try
            {
                Directory.CreateDirectory(fallbackDir);
            }
            catch { }
            string fallback = Path.Combine(fallbackDir, "ZERA.config.xml");
            foreach (string legacyName in LegacyConfigNames)
            {
                Util.MigrateLegacyDataFile(Path.Combine(fallbackDir, legacyName), fallback);
            }
            return fallback;
        }

        /// <summary>
        /// 历史上用过的配置文件名（不含当前名），按"离现在近→远"排序。
        /// 2026-10-14 产品从 ZEKR 改名 ZERA，把 ZEKR.config.xml 也列进来，
        /// 否则这次改名会让老用户的设置（登录态/下载目录/主题）看起来"丢了"。
        /// </summary>
        private static readonly string[] LegacyConfigNames = { "ZEKR.config.xml", "GeZi.config.xml" };

        /// <summary>上一次 Load/Save 是否踩到了异常（供 UI 提示用户设置未能持久化）。</summary>
        public static string LastError { get; private set; }

        public static AppSettings Load()
        {
            LastError = null;
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var loaded = Ser.Deserialize(fs) as AppSettings;
                        if (loaded != null)
                            s = loaded;
                    }
                }
                else
                {
                    // 便携路径没文件时，兜底再看一眼 %LOCALAPPDATA%，实现两种位置互认
                    string alt = Path.Combine(Util.GetLocalAppDataDir(), "ZERA.config.xml");
                    if (File.Exists(alt))
                    {
                        using (var fs = File.OpenRead(alt))
                        {
                            var loaded = Ser.Deserialize(fs) as AppSettings;
                            if (loaded != null)
                                s = loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            // 兼容旧版明文 Cookie：解出来是明文就原样用，下次 Save 时会被自动加密
            s.Cookie = SecretProtector.Unprotect(s.Cookie);

            // 【2026-10-14 多平台】各平台 Cookie 同样解密。
            if (s.PanelCookies == null) s.PanelCookies = new List<PanelCookieEntry>();
            foreach (var e in s.PanelCookies)
                e.Cookie = SecretProtector.Unprotect(e.Cookie);

            // 老配置（升级前）只有 Cookie 字段，没有 PanelCookies ——
            // 那份 Cookie 一定属于夸克，补进去，否则用户会发现"升级后登录态没了"。
            if (string.IsNullOrEmpty(s.GetPanelCookie(s.Panel)) && !string.IsNullOrEmpty(s.Cookie))
                s.SetPanelCookie(s.Panel, s.Cookie);
            if (string.IsNullOrEmpty(s.GetAccountKey(s.Panel)))
                s.RememberAccount(s.CurrentAccountKey);
            s.CurrentAccountKey = s.GetAccountKey(s.Panel);
            // 并发上限与核心层的 MaxRealConcurrency 保持一致。
            // 旧配置里可能存着 512/8192 这类超出范围的值，读进来时夹到有效范围，
            // 免得 UI 上落不到任何一个合法档位。上限取自核心层常量，避免两处失配。
            if (s.Threads <= 0) s.Threads = 64;
            if (s.Threads > ZERA.Core.Download.SegmentedDownloader.MaxRealConcurrency)
                s.Threads = ZERA.Core.Download.SegmentedDownloader.MaxRealConcurrency;
            if (s.ConcurrentTasks <= 0) s.ConcurrentTasks = 3;
            return s;
        }

        public static void Save(AppSettings s)
        {
            LastError = null;
            if (s == null)
                return;

            string rawCookie = s.Cookie;   // 内存中始终明文，落盘前临时加密

            // 各平台 Cookie 也要临时加密（内存明文 → 落盘密文）
            var rawPanel = new List<PanelCookieEntry>();
            if (s.PanelCookies != null)
            {
                foreach (var e in s.PanelCookies)
                {
                    rawPanel.Add(new PanelCookieEntry { Panel = e.Panel, Cookie = e.Cookie });
                }
            }

            var tmpPath = FilePath + ".tmp";
            try
            {
                s.Cookie = s.Panel == PanelKind.WebDav ? SecretProtector.ProtectRequired(rawCookie) : SecretProtector.Protect(rawCookie);
                if (s.PanelCookies != null)
                    foreach (var entry in s.PanelCookies)
                        entry.Cookie = entry.Panel == PanelKind.WebDav ? SecretProtector.ProtectRequired(entry.Cookie) : SecretProtector.Protect(entry.Cookie);

                // 先写临时文件再原子替换：避免写一半断电/异常留下半截 XML，
                // 下次启动 Load 直接抛异常、用户设置全丢。
                using (var fs = File.Create(tmpPath))
                    Ser.Serialize(fs, s);

                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmpPath, FilePath);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            finally
            {
                s.Cookie = rawCookie;   // 还原内存明文，调用方拿到的对象不被污染
                // 同理还原各平台 Cookie，否则调用方保存后内存里就只剩密文了。
                if (s.PanelCookies != null)
                {
                    foreach (var saved in rawPanel)
                        foreach (var e in s.PanelCookies)
                            if (e.Panel == saved.Panel) e.Cookie = saved.Cookie;
                }
            }
        }
    }
}
