using System;
using System.Linq;
using System.Threading.Tasks;
using ZERA.Core.Api;
using ZERA.Core.Download;
using ZERA.Core.Panels;

namespace ZERA
{
    public partial class MainWindow
    {
        private static bool AccountPanel(AccountProfile profile, out PanelKind panel)
        {
            // Profiles predating multi-platform support belong to Quark.
            string name = string.IsNullOrEmpty(profile?.Panel) ? "Quark" : profile.Panel;
            panel = PanelKind.Quark;
            if (name.Equals("Quark", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("Baidu", StringComparison.OrdinalIgnoreCase)) { panel = PanelKind.Baidu; return true; }
            if (name.Equals("WebDav", StringComparison.OrdinalIgnoreCase)) { panel = PanelKind.WebDav; return true; }
            return false;
        }

        private bool HasUnfinishedPanelTasks(PanelKind panel) => _tasks.Any(t =>
            (t.Job.Pending == null || t.Job.Pending.Panel == panel) &&
            (t.State == JobState.Downloading || t.State == JobState.Queued ||
             t.State == JobState.Paused || t.State == JobState.Cancelling));

        private bool CanReplaceAccount(PanelKind target, string cookie)
        {
            if (string.Equals(cookie, PanelCookie(target), StringComparison.Ordinal) || !HasUnfinishedPanelTasks(target)) return true;
            Log("目标网盘还有下载任务，请先完成或取消任务，再更换该平台的账号，避免用错账号刷新下载链接。");
            return false;
        }

        private Task<bool> SwitchRememberedAccountAsync(AccountProfile account)
        {
            if (account == null || !AccountPanel(account, out var target))
            { Log("该账号的网盘平台尚未支持，不能切换。"); return Task.FromResult(false); }
            return ApplyLoginForPanelAsync(account.Cookie, target);
        }

        // Called only after the destination account has passed validation.
        private void ActivateAccountClient(IPanelClient client)
        {
            var target = client.Kind;
            if (_panelClients.TryGetValue(target, out var previous) && previous != client)
            {
                try { previous.ShutdownPlatformResources(); } catch { }
                previous.Dispose();
            }
            // Keep other platforms' clients alive for their downloads and link renewal.
            _panelClients[target] = client;
            _client = client;
            _panel = _settings.Panel = target;
            _settings.Cookie = PanelCookie(target);
            _settings.CurrentAccountKey = _settings.GetAccountKey(target);
            _suppressPanelChanged = true;
            try
            {
                QuarkPanelRadio.IsChecked = target == PanelKind.Quark;
                BaiduPanelRadio.IsChecked = target == PanelKind.Baidu;
                WebDavPanelRadio.IsChecked = target == PanelKind.WebDav;
            }
            finally { _suppressPanelChanged = false; }
            UpdatePanelDependentUi();
        }

        private bool RemoveRememberedAccount(AccountProfile account)
        {
            if (account == null) return false;
            if (!AccountPanel(account, out var target)) return AccountStore.Remove(account.Key);
            if (HasUnfinishedPanelTasks(target))
            { Log("该平台还有下载任务，请先完成或取消，再删除账号。"); return false; }
            bool remembered = _settings.GetAccountKey(target) == account.Key ||
                !string.IsNullOrEmpty(account.Cookie) && PanelCookie(target) == account.Cookie;
            if (!AccountStore.Remove(account.Key)) return false;
            if (remembered)
            {
                if (target == _panel) DoLogout("已删除当前账号，同时退出登录。");
                else
                {
                    _settings.SetPanelCookie(target, "");
                    _settings.PanelCookies.Find(e => e.Panel == target).AccountKey = "";
                    if (_panelClients.TryGetValue(target, out var cached))
                    {
                        try { cached.ShutdownPlatformResources(); } catch { }
                        cached.Dispose(); _panelClients.Remove(target);
                    }
                    SaveSettingsQuiet();
                }
            }
            lock (_accountClientsLock)
                if (_accountClients.TryGetValue(target + "|" + account.Key, out var cached))
                { cached.Dispose(); _accountClients.Remove(target + "|" + account.Key); }
            return true;
        }
    }
}
