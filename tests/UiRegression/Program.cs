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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZERA;
using ZERA.Core.Panels;

internal static class Program
{
    private sealed class LoginServer : HttpMessageHandler
    {
        public int Polls, Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.RequestUri.AbsolutePath.Contains("/get/status"))
            {
                Polls++;
                try { await Task.Delay(30000, cancel); } catch (OperationCanceledException) { Cancelled++; throw; }
            }
            string value = request.RequestUri.Host == "login.123pan.com" ? "{\"code\":200,\"data\":{\"token\":\"UI-TOKEN\"}}" :
                "{\"state\":true,\"data\":{\"uid\":\"ui-session\",\"time\":123,\"sign\":\"signature\",\"qrcode\":\"https://115.com/scan?fixture=1\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) };
        }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Settle()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (s, e) => frame.Continue = false;
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); Settle();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".png"))) encoder.Save(file);
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T;
    private static bool Eventually(Func<bool> condition)
    {
        for (int step = 0; step < 10; step++) { if (condition()) return true; Settle(); }
        return condition();
    }
    private static void Click(MainWindow window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void VerifyDrawers(MainWindow main, string theme)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var open = typeof(MainWindow).GetMethod("OpenWorkspaceDrawer", flags);
        var overlay = Control<Grid>(main, "DrawerOverlay");
        var workspace = Control<Grid>(main, "MainWorkspace");
        var nav = Control<ListBox>(main, "NavList");
        nav.SelectedIndex = 0; Settle();
        var origin = Control<Button>(main, "DownloadMoreButton"); origin.Focus(); Click(main, "DownloadMoreButton"); Settle();
        Check(overlay.IsVisible && !workspace.IsEnabled, "Opening drawer blocks background interaction");
        for (int step = 0; step < 12; step++)
        {
            (Keyboard.FocusedElement as FrameworkElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(Control<Border>(main, "DrawerSurface").IsKeyboardFocusWithin, "Tab stays inside the open drawer");
        }
        Check(Control<Button>(main, "DownloadAllBtn").IsVisible && Control<Button>(main, "FetchLinksBtn").IsVisible, "Secondary download actions remain reachable");
        Click(main, "DrawerCloseButton"); Settle();
        Check(!overlay.IsVisible && workspace.IsEnabled && Keyboard.FocusedElement == origin, "Closing restores background and origin focus");
        foreach (string section in new[] { "download", "tasks", "logs", "settings", "cache", "player", "account" })
        {
            open.Invoke(main, new object[] { section }); Settle();
            Check(Control<Border>(main, "DrawerSurface").ActualWidth <= main.ActualWidth - 24, "Drawer fits the window");
            var transform = (TranslateTransform)Control<Border>(main, "DrawerSurface").RenderTransform;
            Check(Eventually(() => transform.X == 0 && !transform.HasAnimatedProperties), "Drawer opening animation releases clock");
            if (section == "settings")
            {
                var threads = Control<ComboBox>(main, "ThreadsBox"); threads.SelectedIndex = (threads.SelectedIndex + 1) % threads.Items.Count;
                Check(overlay.IsVisible, "Changing a setting never navigates away or closes drawer");
                var concurrent = Control<TextBox>(main, "ConcurrentBox"); concurrent.Focus(); concurrent.Text = "4";
            }
            Capture(main, theme + "-drawer-" + section);
            Click(main, "DrawerCloseButton"); Settle();
            if (section == "settings") Check(SettingsStore.Load().ConcurrentTasks == 4, "Closing commits focused settings input");
        }
        Click(main, "DownloadMoreButton");
        main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent }); Settle();
        Check(!overlay.IsVisible && workspace.IsEnabled, "Escape closes drawer");
        Click(main, "DownloadMoreButton");
        ((Border)overlay.Children[0]).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent }); Settle();
        Check(!overlay.IsVisible && workspace.IsEnabled, "Backdrop closes drawer");
        Click(main, "DownloadMoreButton"); Click(main, "DownloadAllBtn"); Settle();
        Check(!overlay.IsVisible && workspace.IsEnabled, "Executing a download action returns to the file list");
        typeof(MainWindow).GetMethod("Log", flags).Invoke(main, new object[] { "UI feedback fixture" });
        typeof(MainWindow).GetMethod("FlushLogs", flags).Invoke(main, null);
        Check(Control<TextBlock>(main, "ActivityStatusText").Text.Contains("UI feedback fixture"), "Action feedback remains visible while logs are folded away");
        Click(main, "DownloadMoreButton");
        Control<TabControl>(main, "Tabs").SelectedIndex = 3; Settle();
        Check(!overlay.IsVisible && workspace.IsEnabled && nav.SelectedIndex == 3, "Programmatic page navigation dismisses drawer");
        var picker = Control<ComboBox>(main, "AccountPanelBox");
        picker.SelectedIndex = 1; Settle();
        Check(Equals(Control<ComboBox>(main, "DownloadPanelBox").SelectedValue, ZERA.Core.Panels.PanelKind.Baidu), "Account platform picker synchronizes download platform");
        Check(Control<Border>(main, "BaiduCookieCard").Visibility == Visibility.Visible, "Baidu import remains available");
        Click(main, "AccountMoreButton"); Settle();
        Check(Control<Button>(main, "ImportCookieFileBtn").IsVisible && Control<Button>(main, "ExportCookieBtn").IsVisible, "Baidu file import/export preserved");
        Click(main, "DrawerCloseButton");
        picker.SelectedIndex = 2; Settle();
        Check(Equals(Control<ComboBox>(main, "DownloadPanelBox").SelectedValue, ZERA.Core.Panels.PanelKind.WebDav) && Control<Button>(main, "AccountMoreButton").Visibility == Visibility.Collapsed, "WebDAV only exposes supported actions");
        Capture(main, theme + "-account-webdav");
        Check(picker.Items.Count == 10 && Control<ComboBox>(main, "DownloadPanelBox").Items.Count == 10, "Both platform pickers expose ten registered providers");
        for (int i = 3; i < 9; i++)
        {
            picker.SelectedIndex = i; Settle();
            Check(Equals(Control<ComboBox>(main, "DownloadPanelBox").SelectedValue, (ZERA.Core.Panels.PanelKind)i), "Native platform selectors stay synchronized");
            var site = picker.Template.FindName("ContentSite", picker) as ContentPresenter;
            Check(site != null && site.Content.ToString() == ZERA.Core.Panels.PanelRegistry.Describe((ZERA.Core.Panels.PanelKind)i).DisplayName, "Selected platform displays its readable name");
            Check(Control<Button>(main, "NativeConfigureButton").IsVisible && Control<Border>(main, "QrCard").Visibility == Visibility.Collapsed && !Control<RadioButton>(main, "ShareModeRadio").IsEnabled, "Native platform exposes authorization and hides unsupported QR/share actions");
            Check(Control<Button>(main, "NativeQrLoginButton").IsVisible == NativeLoginSession.SupportsQr((PanelKind)i) && Control<Button>(main, "NativePasswordLoginButton").IsVisible == NativeLoginSession.SupportsPassword((PanelKind)i), "Provider shows only implemented QR/password methods");
        }
        Capture(main, theme + "-account-native");
        VerifyLanzou(main, theme);
        VerifyNativeLogin(main, theme);
        picker.SelectedIndex = 0; Settle();
        Control<RadioButton>(main, "ShareModeRadio").IsChecked = true;
        nav.SelectedIndex = 0;
    }
    private static void VerifyLanzou(MainWindow main, string theme)
    {
        var picker = Control<ComboBox>(main, "AccountPanelBox");
        picker.SelectedValue = PanelKind.Lanzou; Settle();
        Check(Control<RadioButton>(main, "ShareModeRadio").IsChecked == true && !Control<RadioButton>(main, "DriveModeRadio").IsEnabled, "Lanzou only exposes public share browsing");
        Check(Control<Button>(main, "LoginBtn").Visibility == Visibility.Collapsed && Control<Button>(main, "AccountMoreButton").Visibility == Visibility.Collapsed, "Lanzou never offers unsupported account login");
        Capture(main, theme + "-account-lanzou");
        var data = new DataObject(DataFormats.UnicodeText, "链接：https://wwx.lanzoux.com/iFixture 提取码：abcd");
        var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
        Control<TextBox>(main, "PassBox").Text = "";
        typeof(MainWindow).GetMethod("OnLinkBoxPasting", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, new object[] { Control<TextBox>(main, "LinkBox"), paste });
        Check((string)paste.DataObject.GetData(DataFormats.UnicodeText) == "https://wwx.lanzoux.com/iFixture" && Control<TextBox>(main, "PassBox").Text == "abcd", "Pasting Lanzou text preserves original URL and fills password");
        var saved = new ZERA.PendingTask { Panel = PanelKind.Lanzou, Fid = "lanzou:fixture", Dest = "fixture.zip", FromShare = true };
        Check(saved.CanResume, "Persisted Lanzou tasks allow resume without account or transient token");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var clients = (System.Collections.Generic.Dictionary<PanelKind, IPanelClient>)typeof(MainWindow).GetField("_panelClients", flags).GetValue(main);
        clients[PanelKind.Lanzou].Dispose(); clients[PanelKind.Lanzou] = new LanzouClient(new LanzouServer());
        picker.SelectedValue = PanelKind.Quark; Settle();
        Control<ListBox>(main, "NavList").SelectedIndex = 0; Settle();
        Control<TextBox>(main, "LinkBox").Text = "https://wwx.lanzoux.com/iFixture";
        Control<TextBox>(main, "PassBox").Text = "";
        Click(main, "ParseBtn");
        for (int step = 0; step < 12; step++) Settle();
        var files = (System.Collections.IList)typeof(MainWindow).GetField("_files", flags).GetValue(main);
        Check(Equals(Control<ComboBox>(main, "DownloadPanelBox").SelectedValue, PanelKind.Lanzou) && files.Count == 1, "Parse action automatically selects Lanzou and populates the file list");
        var selected = Activator.CreateInstance(typeof(MainWindow).GetMethod("GetDownloadItemsAsync", flags).GetParameters()[0].ParameterType);
        ((System.Collections.IList)selected).Add(files[0]);
        var getItems = (Task)typeof(MainWindow).GetMethod("GetDownloadItemsAsync", flags).Invoke(main, new object[] { selected, false });
        for (int step = 0; step < 20 && !getItems.IsCompleted; step++) Settle();
        getItems.GetAwaiter().GetResult();
        var result = (System.Collections.IList)getItems.GetType().GetProperty("Result").GetValue(getItems);
        var itemType = result[0].GetType();
        Check(result.Count == 1 && (PanelKind)itemType.GetField("Panel").GetValue(result[0]) == PanelKind.Lanzou && (bool)itemType.GetField("FromShare").GetValue(result[0]), "Selected Lanzou files enter the download pipeline as public shares");
        Capture(main, theme + "-download-lanzou");
        picker.SelectedValue = PanelKind.Quark; Settle();
        Check(Control<RadioButton>(main, "DriveModeRadio").IsEnabled && Control<Button>(main, "LoginBtn").Visibility == Visibility.Visible, "Returning from Lanzou restores account and drive controls");
    }
    private sealed class LanzouServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            string path = request.RequestUri.AbsolutePath;
            if (path == "/file/test") { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://cdn.example.com/ui.zip"); return Task.FromResult(redirect); }
            string body = path == "/iFixture" ? "<title>界面验证.zip - 蓝奏云</title><iframe src='/fn'></iframe>" : path == "/fn" ? "$.ajax({url:'/ajaxm.php?file=1',data:{'action':'downprocess','sign':'UI'}});" : "{\"zt\":1,\"dom\":\"https://down.lanzoux.com\",\"url\":\"test\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private static void VerifyNativeLogin(MainWindow main, string theme)
    {
        foreach (var kind in new[] { PanelKind.Aliyun, PanelKind.Pan115, PanelKind.Tianyi })
        {
            var dialog = new NativeLoginWindow(kind, true, new NativeCloudAccount(), account => Task.FromResult(false)) { Owner = main };
            dialog.Show(); Settle();
            Check(dialog.FindName("NativeLoginPassword") == null && dialog.FindName("NativeLoginStart") is Button, "QR dialog does not request an account password");
            if (kind != PanelKind.Tianyi) Check(dialog.FindName("NativeLoginAppId") is TextBox, "Open QR provider exposes required app configuration");
            dialog.Close();
        }
        var fake = new LoginServer(); int commits = 0;
        var qr = new NativeLoginWindow(PanelKind.Pan115, true, new NativeCloudAccount { ClientId = "UI-APP" }, account => { commits++; return Task.FromResult(false); }, (kind, options) => new NativeLoginSession(kind, options, fake)) { Owner = main };
        qr.Show(); Settle(); ((Button)qr.FindName("NativeLoginStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle();
        var source = (BitmapSource)((Image)qr.FindName("NativeLoginImage")).Source;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var memory = new MemoryStream()) { encoder.Save(memory); var decoded = ZERA.Core.Support.QrDecoder.DecodeBytes(memory.ToArray()); Check(decoded.Ok && decoded.Text == "https://115.com/scan?fixture=1", "Displayed QR image decodes to the authorization payload"); }
        Capture(qr, theme + "-native-qr-login");
        for (int i = 0; i < 12 && fake.Polls == 0; i++) Settle();
        Check(fake.Polls > 0, "QR starts polling in the same login transaction"); qr.Close(); Settle();
        Check(fake.Cancelled > 0 && commits == 0, "Closing QR dialog cancels in-flight polling and never saves credentials");
        foreach (var kind in new[] { PanelKind.Pan123, PanelKind.Tianyi, PanelKind.Mobile139, PanelKind.Thunder })
        {
            var dialog = new NativeLoginWindow(kind, false, new NativeCloudAccount(), account => Task.FromResult(false)) { Owner = main };
            dialog.Show(); Settle(); Check(dialog.FindName("NativeLoginUsername") is TextBox && dialog.FindName("NativeLoginPassword") is PasswordBox, "Password form uses masked field");
            Check(((PasswordBox)dialog.FindName("NativeLoginPassword")).Background == Application.Current.FindResource("SurfaceBrush"), "Masked password field follows the current theme");
            Check(Math.Abs(((PasswordBox)dialog.FindName("NativeLoginPassword")).ActualHeight - ((TextBox)dialog.FindName("NativeLoginUsername")).ActualHeight) < 3, "Password and username rows have consistent height");
            if (kind == PanelKind.Tianyi)
            {
                typeof(NativeLoginWindow).GetMethod("ShowResult", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, new object[] { new NativeLoginResult { State = NativeLoginState.SmsRequired, Hint = "Fixture SMS verification" } });
                Check(((Button)dialog.FindName("NativeLoginSendSms")).IsVisible && !((Button)dialog.FindName("NativeLoginSubmit")).IsVisible, "SMS flow requires send action before submitting a code");
                Capture(dialog, theme + "-native-sms-login");
            }
            dialog.Close();
        }
        NativeCloudAccount committed = null;
        var password = new NativeLoginWindow(PanelKind.Pan123, false, new NativeCloudAccount(), async account => { await Task.Delay(50); committed = account; return true; },
            (kind, options) => new NativeLoginSession(kind, options, new LoginServer())) { Owner = main };
        password.Dispatcher.BeginInvoke(new Action(() => {
            ((TextBox)password.FindName("NativeLoginUsername")).Text = "ui-user";
            ((PasswordBox)password.FindName("NativeLoginPassword")).Password = "UI-SECRET-PASSWORD";
            ((Button)password.FindName("NativeLoginStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Check(password.ShowDialog() == true && committed != null && committed.LoginMode == "web" && !committed.Serialize().Contains("UI-SECRET-PASSWORD"), "Password result commits through async account validation callback");
        Check(((PasswordBox)password.FindName("NativeLoginPassword")).Password == "", "Password field cleared after successful login");
    }
    [STAThread]
    private static void Main()
    {
        try
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            Check(root.Contains("ui-regression"), "Isolated test directory required");
            File.WriteAllText(Path.Combine(root, "ZERA.config.xml"), "<AppSettings/>");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Colors.Light", "Brushes", "Starfield.Light", "Icons", "ScrollBar", "Styles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri("pack://application:,,,/ZERA;component/Resources/" + name + ".xaml") });
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var main = new MainWindow(); main.Show(); Settle();
            var nav = (ListBox)main.FindName("NavList"); var tabs = (TabControl)main.FindName("Tabs");
            foreach (string theme in new[] { "Light", "Dark" })
            {
                var manager = typeof(MainWindow).Assembly.GetType("ZERA.ThemeManager");
                var setTheme = manager.GetMethod("SetTheme");
                setTheme.Invoke(null, new[] { Enum.Parse(setTheme.GetParameters()[0].ParameterType, theme) });
                Check(((SolidColorBrush)app.FindResource("SurfaceBrush")).Color == (Color)app.FindResource("SurfaceColor"), "Theme updates actual brushes as well as color tokens");
                for (int page = 0; page < nav.Items.Count; page++)
                {
                    nav.SelectedIndex = page; main.UpdateLayout(); Settle();
                    Check(tabs.SelectedIndex == page, "Navigation/content selection must agree");
                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        var item = (TabItem)tabs.Items[i]; item.ApplyTemplate();
                        var body = (Border)item.Template.FindName("Bd", item);
                        Check(body.IsVisible == (i == page), "Only selected page is visible");
                        Check(Eventually(() => body.Opacity == 1 && !body.HasAnimatedProperties), "Page transition releases animation after settling");
                    }
                    Capture(main, theme + "-page-" + page);
                }
                main.Width = main.MinWidth; main.Height = main.MinHeight;
                nav.SelectedIndex = 0; Capture(main, theme + "-minimum");
                VerifyDrawers(main, theme);
                main.Width = 1120; main.Height = 780;
            }
            var action = new Button { Content = "交互验证", Style = (Style)app.FindResource("PrimaryButton") };
            var host = new Window { Content = action, Width = 240, Height = 100, ShowInTaskbar = false };
            host.Show(); Settle(); action.ApplyTemplate();
            var surface = (FrameworkElement)VisualTreeHelper.GetChild(action, 0);
            double width = action.ActualWidth; int clicks = 0;
            action.Click += (s, e) => clicks++;
            surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Settle();
            Check(action.ActualWidth == width, "Hover never changes layout size");
            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            action.IsEnabled = false; Settle();
            var transform = (ScaleTransform)surface.RenderTransform;
            Check(transform.ScaleX == 1 && !transform.HasAnimatedProperties, "Disabling mid-press clears transient transform");
            action.IsEnabled = true; action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(clicks == 1, "Button remains operable");
            host.Hide(); Settle();
            Console.WriteLine("PASS: light/dark four-page rendering, navigation visibility, minimum-window captures, completed animation cleanup, stable layout, disable-mid-press reset, button operation");
            Console.WriteLine("Windows client animations enabled: " + SystemParameters.ClientAreaAnimation);
            Console.WriteLine("PASS: seven drawers in both themes at minimum size; open/close/Escape/backdrop, focus restoration, page navigation, settings save, account platform sync and supported actions");
            Environment.Exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
    }
}
