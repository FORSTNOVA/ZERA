using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZERA;

internal static class Program
{
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
            Check(transform.X == 0 && !transform.HasAnimatedProperties, "Drawer opening animation releases clock");
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
        Check(Control<RadioButton>(main, "BaiduPanelRadio").IsChecked == true, "Account platform picker synchronizes download platform");
        Check(Control<Border>(main, "BaiduCookieCard").Visibility == Visibility.Visible, "Baidu import remains available");
        Click(main, "AccountMoreButton"); Settle();
        Check(Control<Button>(main, "ImportCookieFileBtn").IsVisible && Control<Button>(main, "ExportCookieBtn").IsVisible, "Baidu file import/export preserved");
        Click(main, "DrawerCloseButton");
        picker.SelectedIndex = 2; Settle();
        Check(Control<RadioButton>(main, "WebDavPanelRadio").IsChecked == true && Control<Button>(main, "AccountMoreButton").Visibility == Visibility.Collapsed, "WebDAV only exposes supported actions");
        Capture(main, theme + "-account-webdav");
        picker.SelectedIndex = 0; Settle();
        Control<RadioButton>(main, "ShareModeRadio").IsChecked = true;
        nav.SelectedIndex = 0;
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
                        Check(body.Opacity == 1 && !body.HasAnimatedProperties, "Page transition releases animation after settling");
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
