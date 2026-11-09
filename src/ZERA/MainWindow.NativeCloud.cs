using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA
{
    public partial class MainWindow
    {
        private void OnNativeQrLogin(object sender, RoutedEventArgs e) => OpenNativeLogin(true);
        private void OnNativePasswordLogin(object sender, RoutedEventArgs e) => OpenNativeLogin(false);
        private void OpenNativeLogin(bool qr)
        {
            if (_loginInProgress || !NativeCloudAccount.Supports(_panel)) return;
            var kind = _panel;
            if (HasUnfinishedPanelTasks(kind)) { Log("该平台还有未结束的下载任务，请先完成或取消任务再重新登录，避免替换任务原账号。"); return; }
            var options = new NativeCloudAccount(); string previous = PanelCookie(kind);
            if (!string.IsNullOrEmpty(previous)) try { options = NativeCloudAccount.Parse(previous); } catch { }
            var dialog = new NativeLoginWindow(kind, qr, options, account => ApplyLoginForPanelAsync(account.Serialize(), kind)) { Owner = this };
            dialog.ShowDialog();
        }
        private void OnConfigureNativeCloud(object sender, RoutedEventArgs e)
        {
            if (_loginInProgress || !NativeCloudAccount.Supports(_panel)) return;
            var kind = _panel; var account = new NativeCloudAccount();
            string previous = PanelCookie(kind);
            if (!string.IsNullOrEmpty(previous)) try { account = NativeCloudAccount.Parse(previous); } catch { }
            var window = new Window { Owner = this, Title = PanelDisplayName(kind) + " · 原生授权", ResizeMode = ResizeMode.CanResize };
            DialogChrome.StyleWindow(window, 650, 680);
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(DialogChrome.PageTitle(PanelDisplayName(kind) + "授权"));
            panel.Children.Add(DialogChrome.Hint(NativeCloudAccount.Guide(kind), 8, 12));
            panel.Children.Add(DialogChrome.Hint("此处为高级凭据导入。可用的扫码 / 密码登录在账号页单独提供；登录凭据直接用于对应平台 API，并由 Windows DPAPI 加密保存在本机。", 0, 12));
            var protocol = new ComboBox { ItemsSource = new[] { "开放平台令牌", "网站登录令牌" }, SelectedIndex = account.LoginMode == "web" ? 1 : 0, Style = TryFindResource("AppComboBox") as Style };
            if (kind == PanelKind.Pan123) { panel.Children.Add(DialogChrome.Hint("123 令牌来源（必须与令牌对应）", 8, 4)); panel.Children.Add(protocol); }
            var name = new TextBox { Text = account.Name, Style = TryFindResource("AppTextBox") as Style };
            panel.Children.Add(DialogChrome.Hint("自定义账号名称（可选）", 8, 4)); panel.Children.Add(name);
            var fields = new Dictionary<string, PasswordBox>();
            Action<string, string, string> add = (key, title, value) => {
                panel.Children.Add(DialogChrome.Hint(title, 10, 4));
                var box = new PasswordBox { Password = value ?? "", Style = TryFindResource("AppPasswordBox") as Style };
                System.Windows.Automation.AutomationProperties.SetName(box, title);
                fields.Add(key, box); panel.Children.Add(box);
            };
            if (kind == PanelKind.Mobile139) add("Authorization", "Authorization（可含 Basic 前缀）", account.Authorization);
            else if (kind == PanelKind.Tianyi)
            {
                add("AccessToken", "PC Access Token（或填写下方两个会话字段）", account.AccessToken);
                add("SessionKey", "Session Key", account.SessionKey); add("SessionSecret", "Session Secret", account.SessionSecret);
            }
            else
            {
                add("AccessToken", "开放平台 / 当前会话 Access Token", account.AccessToken);
                if (kind != PanelKind.Pan123) add("RefreshToken", "Refresh Token（可选，用于续期）", account.RefreshToken);
                if (kind != PanelKind.Pan115)
                { add("ClientId", "同一授权应用的 Client ID（可选）", account.ClientId); add("ClientSecret", "同一授权应用的 Client Secret（可选）", account.ClientSecret); }
                if (kind == PanelKind.Thunder)
                { add("DeviceId", "当前会话 Device ID", account.DeviceId); add("CaptchaToken", "当前会话 Captcha Token", account.CaptchaToken); }
            }
            var ua = new TextBox { Text = account.UserAgent, Style = TryFindResource("AppTextBox") as Style };
            var downloadUa = new TextBox { Text = account.DownloadUserAgent, Style = TryFindResource("AppTextBox") as Style };
            if (kind == PanelKind.Pan115 || kind == PanelKind.Thunder)
            { panel.Children.Add(DialogChrome.Hint("API User-Agent（应与授权会话匹配）", 10, 4)); panel.Children.Add(ua); }
            if (kind == PanelKind.Thunder) { panel.Children.Add(DialogChrome.Hint("下载 User-Agent（空则使用上方值）", 10, 4)); panel.Children.Add(downloadUa); }
            var status = DialogChrome.Hint("保存前会连接平台验证账号身份；验证失败不会替换当前账号。", 12, 12); panel.Children.Add(status);
            var save = DialogChrome.PrimaryButton("验证授权并记忆账号"); var close = DialogChrome.SecondaryButton("取消");
            panel.Children.Add(DialogChrome.ButtonRow(save, close)); bool connecting = false;
            window.Closing += (s, args) => { if (connecting) args.Cancel = true; };
            save.Click += async (s, args) => {
                try
                {
                    var value = new NativeCloudAccount { Name = name.Text.Trim(), UserAgent = ua.Text.Trim(), DownloadUserAgent = downloadUa.Text.Trim(), LoginMode = kind == PanelKind.Pan123 ? protocol.SelectedIndex == 1 ? "web" : "" : account.LoginMode, UserFinger = account.UserFinger };
                    foreach (var field in fields) typeof(NativeCloudAccount).GetProperty(field.Key).SetValue(value, field.Value.Password.Trim());
                    value.Validate(kind); string credential = value.Serialize();
                    SecretProtector.ProtectRequired(credential);
                    connecting = true; panel.IsEnabled = false; save.Content = "验证中…";
                    if (await ApplyLoginForPanelAsync(credential, kind)) { connecting = false; window.DialogResult = true; }
                    else status.Text = "验证失败，详情请查看主窗口日志。请核对凭据类型、有效期及接口权限后重试。";
                }
                catch (Exception ex) { status.Text = ex.Message; }
                finally { connecting = false; panel.IsEnabled = true; save.Content = "验证授权并记忆账号"; }
            };
            close.Click += (s, args) => window.Close();
            window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            window.ShowDialog();
        }

        // Capture the original account key: a background renewal must never overwrite another account.
        private void WatchNativeCredentials(IPanelClient client, string key, string nick)
        {
            if (!(client is NativeCloudClient native) || string.IsNullOrEmpty(key)) return;
            native.CredentialsChanged += () => {
                string credential = native.CookieStr;
                // Persist rotating refresh tokens synchronously before the API operation completes.
                if (AccountStore.Get(key) != null)
                {
                    AccountStore.Save(key, nick, credential, native.Kind.ToString());
                    if (AccountStore.Get(key)?.Cookie != credential) throw new InvalidOperationException("更新后的网盘授权未能加密保存，请检查程序目录写入权限。");
                }
                Dispatcher.BeginInvoke(new Action(() => {
                    if (_settings.GetAccountKey(native.Kind) != key) return;
                    _settings.SetPanelCookie(native.Kind, credential);
                    if (_panel == native.Kind) _settings.Cookie = credential;
                    SaveSettingsQuiet();
                }));
            };
        }
    }
}
