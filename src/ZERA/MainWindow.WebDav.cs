using System;
using System.Windows;
using System.Windows.Controls;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA
{
    public partial class MainWindow
    {
        private async void OnConfigureWebDav(object sender, RoutedEventArgs e)
        {
            if (_loginInProgress) return;
            var account = new WebDavAccount();
            string previous = PanelCookie(PanelKind.WebDav);
            if (!string.IsNullOrEmpty(previous)) try { account = WebDavAccount.Parse(previous); } catch { }
            var window = new Window { Owner = this, Title = "接入 WebDAV", ResizeMode = ResizeMode.CanResize };
            DialogChrome.StyleWindow(window, 570, 510);
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(DialogChrome.PageTitle("WebDAV 连接"));
            var name = new TextBox { Text = account.Name };
            var endpoint = new TextBox { Text = account.Endpoint };
            var username = new TextBox { Text = account.Username };
            var password = new PasswordBox { Password = account.Password, Padding = new Thickness(8) };
            foreach (var item in new[] { Tuple.Create("连接 / 账号名称", name), Tuple.Create("WebDAV 根地址（含路径）", endpoint), Tuple.Create("用户名（可留空使用匿名服务）", username) })
            {
                panel.Children.Add(DialogChrome.Hint(item.Item1, 8, 4));
                item.Item2.Style = TryFindResource("AppTextBox") as Style; panel.Children.Add(item.Item2);
            }
            panel.Children.Add(DialogChrome.Hint("密码 / 应用专用密码", 8, 4)); panel.Children.Add(password);
            var http = new CheckBox { Content = "允许 HTTP 未加密连接（账号密码会通过明文网络传输）", IsChecked = account.AllowHttp, Margin = new Thickness(0, 12, 0, 8), Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") };
            panel.Children.Add(http);
            panel.Children.Add(DialogChrome.Hint("支持标准 PROPFIND 和 Basic 认证，使用服务器最终地址；证书必须有效。只读取目录与下载文件，不修改远端数据。", 0, 10));
            var save = DialogChrome.PrimaryButton("测试连接并保存"); var cancel = DialogChrome.SecondaryButton("取消");
            panel.Children.Add(DialogChrome.ButtonRow(save, cancel));
            bool connecting = false;
            window.Closing += (s, args) => { if (connecting) args.Cancel = true; };
            save.Click += async (s, args) => {
                try
                {
                    string credential = new WebDavAccount { Name = name.Text, Endpoint = endpoint.Text, Username = username.Text,
                        Password = password.Password, AllowHttp = http.IsChecked == true }.Serialize();
                    if (!SecretProtector.IsProtected(SecretProtector.Protect(credential))) throw new InvalidOperationException("本机凭据加密不可用，不能保存 WebDAV 密码。");
                    connecting = true; panel.IsEnabled = false; save.Content = "连接中…";
                    bool ok = await ApplyLoginForPanelAsync(credential, PanelKind.WebDav);
                    connecting = false;
                    if (ok) window.DialogResult = true;
                    else AppDialog.Show(window, "连接未通过，原账号和配置保持不变。请核对 WebDAV 根地址、认证方式、账号权限及证书。", "WebDAV 连接失败");
                }
                catch (Exception ex) { AppDialog.Show(window, ex.Message, "WebDAV 配置"); }
                finally { connecting = false; panel.IsEnabled = true; save.Content = "测试连接并保存"; }
            };
            cancel.Click += (s, args) => window.Close();
            window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            if (window.ShowDialog() == true)
            {
                DriveModeRadio.IsChecked = true; await LoadDirAsync("0");
            }
        }
    }
}
