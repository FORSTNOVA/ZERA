using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZERA.Core.Panels;

namespace ZERA
{
    public partial class MainWindow
    {
        private IInputElement _drawerReturnFocus;
        private bool _syncingAccountPicker;

        private void OnOpenDrawer(object sender, RoutedEventArgs e)
        {
            string section = (sender as FrameworkElement)?.Tag as string;
            OpenWorkspaceDrawer(section);
        }

        private void OpenWorkspaceDrawer(string section)
        {
            StackPanel content;
            string title, description;
            switch (section)
            {
                case "download": content = DownloadDrawerContent; title = "更多下载操作"; description = "下载当前列表，或获取所选文件的直链与导出格式。"; break;
                case "tasks": content = TasksDrawerContent; title = "任务工具"; description = "查看历史、排查问题，或清理已完成的任务记录。"; break;
                case "logs": content = LogsDrawerContent; title = "运行日志"; description = "解析与下载过程的详细记录；下载失败时可在这里查看原因。"; break;
                case "settings": content = SettingsDrawerContent; title = "下载与存储"; description = "更改即时保存；文本输入在按回车或离开输入框后生效。"; break;
                case "cache": content = CacheDrawerContent; title = "分片与缓存"; description = "设置独立分片的保存位置和目录显示方式。"; break;
                case "player": content = PlayerDrawerContent; title = "播放器"; description = "检查本机播放器，了解边下边播的使用条件。"; break;
                case "account": content = AccountDrawerContent; title = "账号工具"; description = "扫码不方便时可导入 Cookie；会员查询仅在百度平台提供。"; break;
                default: return;
            }
            if (DrawerOverlay.Visibility != Visibility.Visible) _drawerReturnFocus = Keyboard.FocusedElement;
            foreach (var panel in new[] { DownloadDrawerContent, TasksDrawerContent, LogsDrawerContent,
                SettingsDrawerContent, CacheDrawerContent, PlayerDrawerContent, AccountDrawerContent })
                panel.Visibility = panel == content ? Visibility.Visible : Visibility.Collapsed;
            DrawerTitle.Text = title; DrawerDescription.Text = description;
            DrawerSurface.Width = Math.Min(560, Math.Max(320, ActualWidth - 32));
            DrawerScroll.ScrollToTop();
            DrawerOverlay.Visibility = Visibility.Visible;
            MainWorkspace.IsEnabled = false;
            DrawerCloseButton.Focus();
            if (section == "logs")
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => LogBox.ScrollToEnd()));
            var transform = (TranslateTransform)DrawerSurface.RenderTransform;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            if (SystemParameters.ClientAreaAnimation)
            {
                var animation = new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(180)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
                animation.Completed += (s, args) => transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }

        private void CloseWorkspaceDrawer()
        {
            if (DrawerOverlay == null || DrawerOverlay.Visibility != Visibility.Visible) return;
            // Let focused text inputs commit settings before hiding their visual tree.
            DrawerCloseButton.Focus();
            DrawerOverlay.Visibility = Visibility.Collapsed;
            ((TranslateTransform)DrawerSurface.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
            MainWorkspace.IsEnabled = true;
            var focus = _drawerReturnFocus as UIElement;
            if (focus != null && focus.IsVisible && focus.IsEnabled) Keyboard.Focus(focus);
            _drawerReturnFocus = null;
        }

        private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseWorkspaceDrawer();
        private void OnDrawerSurfaceAction(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(e.Source, DownloadAllBtn) || ReferenceEquals(e.Source, FetchLinksBtn))
                CloseWorkspaceDrawer();
        }
        private void OnDrawerBackdropClick(object sender, MouseButtonEventArgs e) { CloseWorkspaceDrawer(); e.Handled = true; }
        private void OnWorkspaceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || DrawerOverlay.Visibility != Visibility.Visible) return;
            CloseWorkspaceDrawer(); e.Handled = true;
        }

        private void OnAccountPanelSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingAccountPicker || QuarkPanelRadio == null || AccountPanelBox.SelectedIndex < 0) return;
            var target = new[] { QuarkPanelRadio, BaiduPanelRadio, WebDavPanelRadio }[AccountPanelBox.SelectedIndex];
            target.IsChecked = true;
        }

        private void SyncWorkspaceAccountUi()
        {
            if (AccountPanelBox == null) return;
            _syncingAccountPicker = true;
            try { AccountPanelBox.SelectedIndex = (int)_panel; }
            finally { _syncingAccountPicker = false; }
            LoginBtn.Content = _panel == PanelKind.WebDav ? "连接 WebDAV" : "扫码登录";
            AccountMoreButton.Content = _panel == PanelKind.Baidu ? "Cookie / 会员查询…" : "更多登录方式…";
            AccountMoreButton.Visibility = _panel == PanelKind.WebDav ? Visibility.Collapsed : Visibility.Visible;
            CookieFileTools.Visibility = _panel == PanelKind.WebDav ? Visibility.Collapsed : Visibility.Visible;
            WebDavConfigureButton.Visibility = _panel == PanelKind.WebDav ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAccountLoginAction(object sender, RoutedEventArgs e)
        {
            if (_panel == PanelKind.WebDav) OnConfigureWebDav(sender, e);
            else
            {
                // One QR entry prevents starting competing polls from duplicate buttons.
                QrLoginBtn.Focus();
                QrLoginBtn.BringIntoView();
                if (QrLoginBtn.IsEnabled) OnQrLogin(QrLoginBtn, e);
            }
        }

        private void OnFileSelectionSummaryChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectionSummaryText == null) return;
            int count = FileList.SelectedItems.Count;
            SelectionSummaryText.Text = count == 0 ? "请选择要下载的文件" : "已选择 " + count + " 项";
        }

        private void OnGoToDownloads(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 0;
    }
}
