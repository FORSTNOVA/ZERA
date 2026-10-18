using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ZERA;
using ZERA.Core.Panels;

internal static class Program
{
    private sealed class IdentityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":200,\"code\":0,\"data\":{\"nickname\":\"same-name\",\"avatarUri\":\"https://image.example/avatar\"}}") });
    }
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame(); var end = DateTime.UtcNow.AddSeconds(30);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (s, e) => { if (task.IsCompleted || DateTime.UtcNow > end) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Check(task.IsCompleted, "Async UI operation timed out"); return task.GetAwaiter().GetResult();
    }
    private sealed class ShareHandler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            Check(body.Contains("saved-share") && body.Contains("saved-token") && !body.Contains("changed-share"), "Task captures original share context");
            Calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":200,\"code\":0,\"data\":[{\"fid\":\"fixture-file\",\"download_url\":\"https://fixture.example/verified-file\"}]}") };
        }
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    [STAThread]
    private static int Main()
    {
        // Tests must run in a dedicated build directory, never against user data.
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (!root.Contains("account-regression")) throw new Exception("Isolated test directory required.");
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => {
                var error = args.ExceptionObject as Exception;
                File.WriteAllText(Path.Combine(root, "unhandled.txt"), error?.GetType().FullName + ": " + error?.Message + "\n" + error?.StackTrace);
            };
            string config = Path.Combine(root, "ZERA.config.xml");
            File.WriteAllText(config, "<AppSettings><CurrentAccountKey>legacy-key</CurrentAccountKey></AppSettings>");
            var s = SettingsStore.Load();
            Check(s.GetAccountKey(PanelKind.Quark) == "legacy-key", "Legacy current account migration");
            s.SetPanelCookie(PanelKind.Quark, "quark-test-cookie");
            s.RememberAccount("quark-id");
            s.Panel = PanelKind.Baidu;
            s.SetPanelCookie(PanelKind.Baidu, "baidu-test-cookie");
            s.RememberAccount("baidu-id");
            SettingsStore.Save(s);
            var restored = SettingsStore.Load();
            Check(restored.Panel == PanelKind.Baidu && restored.CurrentAccountKey == "baidu-id", "Restart current account");
            Check(restored.GetAccountKey(PanelKind.Quark) == "quark-id", "Independent platform account keys");
            Check(restored.GetPanelCookie(PanelKind.Baidu) == "baidu-test-cookie", "Cookie round trip");
            Check(!File.ReadAllText(config).Contains("baidu-test-cookie"), "DPAPI settings encryption");
            restored.RememberAccount("");
            Check(restored.GetAccountKey(PanelKind.Quark) == "quark-id", "Logout preserves other platform");
            AccountStore.Save("legacy", "Old Quark", "old-cookie");
            AccountStore.Save("bd_test", "Baidu", "bd-cookie", "Baidu");
            AccountStore.Save("quark", "Quark", "q-cookie", "Quark");
            AccountStore.Save("quark", "Quark updated", "q-cookie-2", "Quark");
            Check(AccountStore.LoadForPanel("Quark").Count == 2, "Platform filtering and legacy profiles");
            Check(AccountStore.LoadForPanel("Baidu").Count == 1, "Baidu isolation");
            Check(AccountStore.Get("quark").Cookie == "q-cookie-2", "Update saved credentials");
            Check(AccountStore.FindForCredential(PanelKind.Quark, "q-cookie-2").Key == "quark", "Migration reuses legacy profile key when identity lookup falls back");
            Check(!File.ReadAllText(Path.Combine(AccountStore.AccountsDir, "quark.xml")).Contains("q-cookie-2"), "DPAPI profile encryption");
            File.WriteAllText(Path.Combine(AccountStore.AccountsDir, "broken.xml"), "<broken");
            Check(AccountStore.LoadAll().Count == 3, "Corrupt profile isolation");
            Check(AccountStore.Remove("legacy"), "Remove remembered account");
            Check(AccountStore.Get("bd_test") != null, "Delete preserves other profiles");
            Console.WriteLine("PASS: account encryption, persistence, migration, platform isolation, deletion");

            // Empty local settings prevent startup account/network access.
            SettingsStore.Save(new AppSettings { OutDir = root });
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var name in new[] { "Colors.Light", "Brushes", "Starfield.Light", "Icons", "ScrollBar", "Styles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri("pack://application:,,,/ZERA;component/Resources/" + name + ".xaml") });
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var main = new MainWindow();
            new WindowInteropHelper(main).EnsureHandle();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string[] fallbackKeys = new string[2];
            for (int index = 0; index < fallbackKeys.Length; index++)
            using (var fallbackClient = new ZERA.Core.Api.QuarkClient("__puus=identity-" + index))
            {
                var identitySession = typeof(ZERA.Core.Api.QuarkClient).GetField("_session", flags).GetValue(fallbackClient);
                var identityHttp = identitySession.GetType().GetField("_http", flags);
                ((HttpClient)identityHttp.GetValue(identitySession)).Dispose();
                identityHttp.SetValue(identitySession, new HttpClient(new IdentityHandler()));
                fallbackKeys[index] = Pump(PanelAdapter.For(fallbackClient).LoginAsync()).Key;
            }
            Check(fallbackKeys[0] != fallbackKeys[1] && fallbackKeys.All(key => key != "same-name"), "Missing provider IDs never merge same-named accounts");
            Check(((Button)main.FindName("AccountsBtn")).Visibility == Visibility.Visible, "Account switch entry available while logged out");
            Check(main.FindName("BaiduMembershipButton") is Button, "Baidu membership query entry");
            var settings = (AppSettings)typeof(MainWindow).GetField("_settings", flags).GetValue(main);
            settings.Panel = PanelKind.Baidu;
            settings.Who = "Keep original";
            settings.CurrentAccountKey = "original";
            typeof(MainWindow).GetField("_panel", flags).SetValue(main, PanelKind.Baidu);
            var failedLogin = (Task<bool>)typeof(MainWindow).GetMethod("ApplyLoginAsync", flags).Invoke(main, new object[] { "" });
            Check(!failedLogin.GetAwaiter().GetResult(), "Invalid login rejected");
            Check(settings.Who == "Keep original" && settings.CurrentAccountKey == "original", "Failed switch preserves current account");
            Console.WriteLine("PASS: failed switch preserves current account");
            var rowMethod = typeof(MainWindow).GetMethod("BuildAccountRow", flags);
            var currentRow = (Border)rowMethod.Invoke(main, new object[] {
                AccountStore.Get("bd_test"), "bd_test", "", main, new Action(() => { }) });
            Check(!((Button)((Grid)currentRow.Child).Children[1]).IsEnabled, "Current account cannot be selected again");
            Console.WriteLine("PASS: current account selection disabled");
            var ensure = typeof(MainWindow).GetMethod("EnsureClient", flags);
            var panel = typeof(MainWindow).GetField("_panel", flags);
            var clientField = typeof(MainWindow).GetField("_client", flags);
            ensure.Invoke(main, null);
            var baiduClient = clientField.GetValue(main);
            panel.SetValue(main, PanelKind.Quark);
            ensure.Invoke(main, null);
            panel.SetValue(main, PanelKind.Baidu);
            ensure.Invoke(main, null);
            Check(ReferenceEquals(baiduClient, clientField.GetValue(main)), "Platform switch preserves cached account session");
            Console.WriteLine("PASS: platform session cache");
            var tasks = (System.Collections.IList)typeof(MainWindow).GetField("_tasks", flags).GetValue(main);
            tasks.Add(new TaskItem(new ZERA.Core.Download.DownloadJob {
                Pending = new ZERA.Core.Download.PendingInfo { Panel = PanelKind.Baidu }
            }, "test task"));
            Console.WriteLine("PASS: queued task fixture");
            var blocked = (Task<bool>)typeof(MainWindow).GetMethod("ApplyLoginAsync", flags).Invoke(main, new object[] { "different-cookie" });
            Check(!blocked.GetAwaiter().GetResult(), "Active tasks block account change without network access");
            tasks.Clear();
            Console.WriteLine("PASS: active task account switch guard");

            // Inspect the actual modal dialog, including the previously broken rebuild path.
            Exception dialogError = null;
            main.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dialog = app.Windows.Cast<Window>().FirstOrDefault(w => w != main);
                try
                {
                    Check(dialog != null, "Account dialog exists");
                    var grid = (Grid)dialog.Content;
                    var card = (Border)grid.Children[2];
                    var list = (ListBox)card.Child;
                    Check(list.Items.Count == 2, "Dialog shows accounts from both platforms");
                    var labels = list.Items.Cast<Border>().Select(b => (StackPanel)((Grid)b.Child).Children[0]).ToList();
                    Check(labels.Any(p => ((TextBlock)p.Children[0]).Text == "Baidu") &&
                          labels.Any(p => ((TextBlock)p.Children[0]).Text == "Quark updated"), "Cross-platform account rows");
                    Check(labels.Any(p => ((TextBlock)p.Children[1]).Text.Contains("百度")) &&
                          labels.Any(p => ((TextBlock)p.Children[1]).Text.Contains("夸克")), "Platform labels visible");
                }
                catch (Exception ex) { dialogError = ex; }
                finally { dialog?.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            typeof(MainWindow).GetMethod("ShowAccountsDialog", flags).Invoke(main, null);
            if (dialogError != null) throw dialogError;
            Console.WriteLine("PASS: WPF unified account entry, platform labels, failed switch preservation");
            var sameKeyOtherPlatform = (Border)rowMethod.Invoke(main, new object[] {
                new AccountProfile { Key = "bd_test", Panel = "Quark", Cookie = "different" }, "bd_test", "", main, new Action(() => { }) });
            Check(((Button)((Grid)sameKeyOtherPlatform.Child).Children[1]).IsEnabled, "Current account identity includes platform");
            var switchAccount = typeof(MainWindow).GetMethod("SwitchRememberedAccountAsync", flags);
            var originalClient = clientField.GetValue(main);
            var rejected = (Task<bool>)switchAccount.Invoke(main, new object[] { new AccountProfile { Panel = "Quark", Cookie = "" } });
            Check(!rejected.GetAwaiter().GetResult() && panel.GetValue(main).Equals(PanelKind.Baidu) && ReferenceEquals(clientField.GetValue(main), originalClient), "Failed cross-platform switch preserves source client and panel");
            var unsupported = (Task<bool>)switchAccount.Invoke(main, new object[] { new AccountProfile { Panel = "Unknown", Cookie = "test" } });
            Check(!unsupported.GetAwaiter().GetResult(), "Unsupported platform never falls back to Quark");
            var activate = typeof(MainWindow).GetMethod("ActivateAccountClient", flags);
            settings.SetPanelCookie(PanelKind.Quark, "q-test"); settings.Panel = PanelKind.Quark; settings.RememberAccount("quark");
            settings.SetPanelCookie(PanelKind.Baidu, "b-test"); settings.Panel = PanelKind.Baidu; settings.RememberAccount("bd_test");
            var sourceClient = new ZERA.Core.Api.QuarkClient("q-test");
            activate.Invoke(main, new object[] { sourceClient });
            typeof(MainWindow).GetField("_pwdId", flags).SetValue(main, "saved-share");
            typeof(MainWindow).GetField("_stoken", flags).SetValue(main, "saved-token");
            var itemType = typeof(MainWindow).Assembly.GetType("ZERA.DownloadItem");
            var item = Activator.CreateInstance(itemType);
            itemType.GetField("Fid").SetValue(item, "fixture-file");
            itemType.GetField("FromShare").SetValue(item, true);
            var refresh = (Func<CancellationToken, Task<string>>)typeof(MainWindow).GetMethod("CreateLinkRefresher", flags).Invoke(main, new[] { item });
            tasks.Add(new TaskItem(new ZERA.Core.Download.DownloadJob { Pending = new ZERA.Core.Download.PendingInfo { Panel = PanelKind.Baidu } }, "target task"));
            var guarded = (Task<bool>)switchAccount.Invoke(main, new object[] { new AccountProfile { Panel = "Baidu", Cookie = "BDUSS=different" } });
            Check(!guarded.GetAwaiter().GetResult() && panel.GetValue(main).Equals(PanelKind.Quark), "Guard checks destination platform's unfinished tasks");
            tasks.Clear();
            var invalidDestination = (Task<bool>)switchAccount.Invoke(main, new object[] { new AccountProfile { Panel = "Baidu", Cookie = "no-bduss" } });
            Check(!invalidDestination.GetAwaiter().GetResult() && ReferenceEquals(clientField.GetValue(main), sourceClient), "Invalid target validation preserves original platform");
            tasks.Add(new TaskItem(new ZERA.Core.Download.DownloadJob { Pending = new ZERA.Core.Download.PendingInfo { Panel = PanelKind.Quark } }, "source task"));
            var destinationClient = new ZERA.Core.Api.BaiduClient("BDUSS=test-destination");
            activate.Invoke(main, new object[] { destinationClient });
            typeof(MainWindow).GetField("_pwdId", flags).SetValue(main, "changed-share");
            typeof(MainWindow).GetField("_stoken", flags).SetValue(main, "changed-token");
            var shareSession = typeof(ZERA.Core.Api.QuarkClient).GetField("_session", flags).GetValue(sourceClient);
            var shareHttp = shareSession.GetType().GetField("_http", flags);
            ((HttpClient)shareHttp.GetValue(shareSession)).Dispose();
            var shareHandler = new ShareHandler(); shareHttp.SetValue(shareSession, new HttpClient(shareHandler));
            Check(Task.Run(() => refresh(CancellationToken.None)).GetAwaiter().GetResult() == "https://fixture.example/verified-file" && shareHandler.Calls > 0, "Background source link refresh survives platform/context switch");
            var clients = (System.Collections.IDictionary)typeof(MainWindow).GetField("_panelClients", flags).GetValue(main);
            Check(ReferenceEquals(clients[PanelKind.Quark], sourceClient) && ReferenceEquals(clients[PanelKind.Baidu], destinationClient), "Cross-platform commit retains source task client");
            Check(settings.Panel == PanelKind.Baidu && settings.CurrentAccountKey == "bd_test" && ((RadioButton)main.FindName("BaiduPanelRadio")).IsChecked == true, "Validated commit updates platform, UI and remembered key");
            var removeAccount = typeof(MainWindow).GetMethod("RemoveRememberedAccount", flags);
            Check(!(bool)removeAccount.Invoke(main, new object[] { AccountStore.Get("quark") }), "Cannot delete source account while its tasks exist");
            tasks.Clear();
            Check((bool)removeAccount.Invoke(main, new object[] { AccountStore.Get("quark") }), "Remove remembered other-platform account");
            Check(settings.GetPanelCookie(PanelKind.Quark) == "" && settings.GetAccountKey(PanelKind.Quark) == "" && settings.Panel == PanelKind.Baidu && settings.CurrentAccountKey == "bd_test", "Deletion clears only the selected platform's remembered credential");
            SettingsStore.Save(settings);
            var switchedSettings = SettingsStore.Load();
            Check(switchedSettings.Panel == PanelKind.Baidu && switchedSettings.GetAccountKey(PanelKind.Baidu) == "bd_test", "Cross-platform selection persists on restart");
            Console.WriteLine("PASS: cross-platform guards/commit, source-client preservation, failed validation, remembered credential deletion and restart");
            var torrentWindow = new TorrentWindow(root, Path.Combine(root, "bt-state-ui"));
            Check(torrentWindow.FindName("AddTorrentButton") is Button, "Torrent file import entry");
            Check(torrentWindow.FindName("MagnetBox") is TextBox, "Magnet input");
            Check(torrentWindow.FindName("TorrentList") is ListView, "BT task list");
            Check(torrentWindow.FindName("AddSplitButton") is Button, "Independent piece-range import entry");
            Check(((ComboBox)torrentWindow.FindName("SplitCountBox")).Items.Count == 3, "2/4/8 independent worker choices");
            Check(torrentWindow.FindName("TrackersButton") is Button, "Tracker management entry");
            torrentWindow.ShutdownEngine();
            Console.WriteLine("PASS: WPF torrent window resources and download controls");
            var trackerStore = new ZERA.Core.Download.TrackerStore(Path.Combine(root, "tracker-ui-" + Guid.NewGuid().ToString("N")));
            var trackerWindow = new TrackerWindow(trackerStore);
            var trackerList = (ListView)trackerWindow.FindName("SubscriptionsList");
            var nameBox = (TextBox)trackerWindow.FindName("NameBox");
            var urlBox = (TextBox)trackerWindow.FindName("UrlBox");
            var manualBox = (TextBox)trackerWindow.FindName("ManualBox");
            typeof(TrackerWindow).GetMethod("OnDefaults", flags).Invoke(trackerWindow, new object[] { null, null });
            Check(trackerList.Items.Count == 2, "Add common GitHub subscriptions");
            typeof(TrackerWindow).GetMethod("OnNew", flags).Invoke(trackerWindow, new object[] { null, null });
            nameBox.Text = "自定义测试源"; urlBox.Text = "https://github.com/test/repo/blob/main/list.txt";
            typeof(TrackerWindow).GetMethod("OnUpsert", flags).Invoke(trackerWindow, new object[] { null, null });
            Check(trackerList.Items.Count == 3, "Add named source in UI");
            nameBox.Text = "重命名测试源";
            typeof(TrackerWindow).GetMethod("OnUpsert", flags).Invoke(trackerWindow, new object[] { null, null });
            Check(((ZERA.Core.Download.TrackerSubscription)trackerList.SelectedItem).Name == "重命名测试源", "Edit selected subscription");
            trackerList.SelectedIndex = 0;
            typeof(TrackerWindow).GetMethod("OnRemove", flags).Invoke(trackerWindow, new object[] { null, null });
            Check(trackerList.Items.Count == 2, "Remove selected subscription");
            manualBox.Text = "udp://manual.example:80/announce";
            typeof(TrackerWindow).GetMethod("OnSave", flags).Invoke(trackerWindow, new object[] { null, null });
            var trackerSaved = trackerStore.Load();
            Check(trackerSaved.Subscriptions.Count == 2 && trackerSaved.Effective.Contains(manualBox.Text), "UI settings persist");
            var reopened = new TrackerWindow(trackerStore);
            Check(((ListView)reopened.FindName("SubscriptionsList")).Items.Count == 2, "Reopen remembers subscriptions");
            reopened.Close();
            Console.WriteLine("PASS: WPF tracker management add/edit/delete, manual list, GitHub defaults and persistence");
            Check(main.FindName("WebDavConfigureButton") is Button, "WebDAV configuration entry");
            using (var dav = new DavFixture())
            {
                string credential = new WebDavAccount { Endpoint = dav.Root, Username = "user", Password = "SECRET-PASS", Name = "本机 WebDAV", AllowHttp = true }.Serialize();
                var profile = new AccountProfile { Panel = "WebDav", Cookie = credential };
                Check(Pump((Task<bool>)switchAccount.Invoke(main, new object[] { profile })), "Complete unified WebDAV login through real local PROPFIND");
                var activeDav = (IPanelClient)clientField.GetValue(main);
                Check(activeDav.Kind == PanelKind.WebDav && settings.Panel == PanelKind.WebDav && ((RadioButton)main.FindName("WebDavPanelRadio")).IsChecked == true, "WebDAV platform committed");
                var directoryTask = (Task)typeof(MainWindow).GetMethod("LoadDirAsync", flags).Invoke(main, new object[] { "0" });
                Pump(directoryTask.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
                var davFiles = (System.Collections.IList)typeof(MainWindow).GetField("_files", flags).GetValue(main);
                Check(davFiles.Count == 2, "WebDAV files reach the actual shared browser");
                var fileEntry = davFiles.Cast<object>().First(f => !(bool)f.GetType().GetProperty("IsDir").GetValue(f));
                var selection = (System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(fileEntry.GetType())); selection.Add(fileEntry);
                var itemTask = (Task)typeof(MainWindow).GetMethod("DriveGetItemsAsync", flags).Invoke(main, new object[] { selection });
                Pump(itemTask.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
                var davItems = (System.Collections.IList)itemTask.GetType().GetProperty("Result").GetValue(itemTask);
                Check(davItems.Count == 1, "Generic drive download preparation");
                var davContext = (DownloadRequestContext)typeof(MainWindow).GetMethod("BuildContextForItem", flags).Invoke(main, new[] { davItems[0] });
                Check(davContext.Cookie == "" && davContext.Authorization.StartsWith("Basic ") && davContext.CredentialScope == dav.Root, "WebDAV download context reaches existing task pipeline");
                Check(!((RadioButton)main.FindName("ShareModeRadio")).IsEnabled && ((RadioButton)main.FindName("DriveModeRadio")).IsChecked == true, "Unsupported share mode disabled");
                Check(((Border)main.FindName("CookieImportCard")).Visibility == Visibility.Collapsed && ((Border)main.FindName("QrCard")).Visibility == Visibility.Collapsed, "Unsupported cookie/QR UI hidden");
                var savedDav = AccountStore.Get(settings.CurrentAccountKey);
                Check(savedDav != null && savedDav.Cookie.Contains("SECRET-PASS"), "WebDAV credential round trip");
                Check(!File.ReadAllText(SettingsStore.FilePath).Contains("SECRET-PASS"), "Settings password protected");
                Check(!File.ReadAllText(Path.Combine(AccountStore.AccountsDir, AccountStore.SafeKey(savedDav.Key)+".xml")).Contains("SECRET-PASS"), "Profile password protected");
                var restoredDav = SettingsStore.Load();
                Check(restoredDav.Panel == PanelKind.WebDav && restoredDav.GetAccountKey(PanelKind.WebDav) == savedDav.Key, "WebDAV persists alongside legacy platform slots");
                profile.Cookie = credential.Replace("SECRET-PASS", "wrong-password");
                Check(!Pump((Task<bool>)switchAccount.Invoke(main, new object[] { profile })) && ReferenceEquals(clientField.GetValue(main), activeDav), "Wrong WebDAV credentials preserve active client");
                using (var secondDav = new DavFixture())
                {
                    var secondProfile = new AccountProfile { Panel = "WebDav", Cookie = new WebDavAccount { Endpoint = secondDav.Root, Username = "user", Password = "SECRET-PASS", Name = "另一服务器", AllowHttp = true }.Serialize() };
                    Check(Pump((Task<bool>)switchAccount.Invoke(main, new object[] { secondProfile })), "Switch to another WebDAV server");
                    var contextMethod = typeof(MainWindow).GetMethod("BuildContextForPending", flags, null, new[] { typeof(ZERA.Core.Download.PendingInfo) }, null);
                    var originalContext = (DownloadRequestContext)contextMethod.Invoke(main, new object[] { new ZERA.Core.Download.PendingInfo { Panel = PanelKind.WebDav, AccountKey = savedDav.Key, Fid = dav.Root+"file.bin" } });
                    Check(originalContext.CredentialScope == dav.Root, "Pending task uses original account/server after switching current server");
                    var secondSaved = AccountStore.Get(settings.CurrentAccountKey);
                    Check((bool)removeAccount.Invoke(main, new object[] { savedDav }), "Remove original WebDAV remembered credential");
                    Check((bool)removeAccount.Invoke(main, new object[] { secondSaved }), "Remove second WebDAV remembered credential");
                }
            }
            Console.WriteLine("PASS: complete WebDAV UI login, capability gating, encrypted persistence, failed-password preservation and removal");
            Environment.Exit(0);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetType().FullName + ": " + ex.Message);
            Environment.Exit(1);
            return 1;
        }
    }
}
