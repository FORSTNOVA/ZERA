using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA
{
    public sealed class NativeLoginWindow : Window
    {
        private readonly PanelKind _kind;
        private readonly bool _qrMode;
        private readonly NativeCloudAccount _options;
        private readonly Func<NativeCloudAccount, Task<bool>> _commit;
        private readonly Func<PanelKind, NativeCloudAccount, NativeLoginSession> _factory;
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private CancellationTokenSource _attempt;
        private NativeLoginSession _session;
        private readonly TextBox _name = new TextBox(), _appId = new TextBox(), _username = new TextBox(), _code = new TextBox();
        private readonly PasswordBox _password = new PasswordBox(), _secret = new PasswordBox();
        private readonly Image _image = new Image { Width = 240, Height = 240, Stretch = Stretch.Uniform };
        private readonly TextBlock _status;
        private readonly StackPanel _challenge = new StackPanel(), _inputs = new StackPanel();
        private readonly Button _start, _submit, _sendSms, _verify;
        private bool _committing, _busy;
        private string _verificationUrl;
        public NativeLoginWindow(PanelKind kind, bool qrMode, NativeCloudAccount options, Func<NativeCloudAccount, Task<bool>> commit,
            Func<PanelKind, NativeCloudAccount, NativeLoginSession> factory = null)
        {
            _kind = kind; _qrMode = qrMode; _options = NativeCloudAccount.Parse((options ?? new NativeCloudAccount()).Serialize()); _commit = commit;
            if (qrMode ? !NativeLoginSession.SupportsQr(kind) : !NativeLoginSession.SupportsPassword(kind)) throw new NotSupportedException("所选平台暂不支持此登录方式。");
            _factory = factory ?? new Func<PanelKind, NativeCloudAccount, NativeLoginSession>((provider, credential) => new NativeLoginSession(provider, credential));
            NameScope.SetNameScope(this, new NameScope());
            Title = PanelRegistry.Describe(kind).DisplayName + (qrMode ? " · 扫码登录" : " · 密码登录");
            DialogChrome.StyleWindow(this, 610, 720);
            var panelRoot = new StackPanel { Margin = new Thickness(22) };
            panelRoot.Children.Add(DialogChrome.PageTitle(Title));
            panelRoot.Children.Add(DialogChrome.Hint(qrMode ? "使用所选网盘 App 扫码并在手机确认；二维码仅在本次窗口中有效。" : "密码仅用于本次官方认证，不写入本地配置。平台可能要求图片、短信或设备验证。", 8, 12));
            _name.Text = _options.Name; AddInput(_inputs, "账号名称（可选）", _name, "NativeLoginName");
            if (qrMode && (kind == PanelKind.Aliyun || kind == PanelKind.Pan115))
            {
                _appId.Text = _options.ClientId; AddInput(_inputs, kind == PanelKind.Pan115 ? "开放平台 App ID（Client ID，必填）" : "开放平台 Client ID（必填）", _appId, "NativeLoginAppId");
                if (kind == PanelKind.Aliyun) { _secret.Password = _options.ClientSecret; AddInput(_inputs, "同一应用 Client Secret（必填）", _secret, "NativeLoginAppSecret"); }
                _inputs.Children.Add(DialogChrome.Hint("需要已获对应权限的开放平台应用；不会使用 OpenList 的应用或中转服务。", 8, 8));
            }
            if (!qrMode)
            {
                AddInput(_inputs, kind == PanelKind.Mobile139 ? "139 邮箱 / 手机号账号" : "手机号 / 邮箱 / 账号", _username, "NativeLoginUsername");
                AddInput(_inputs, "密码", _password, "NativeLoginPassword");
            }
            panelRoot.Children.Add(_inputs);
            _start = DialogChrome.PrimaryButton(qrMode ? "获取二维码" : "密码登录"); RegisterName("NativeLoginStart", _start);
            var reset = DialogChrome.SecondaryButton("重新开始"); RegisterName("NativeLoginReset", reset);
            panelRoot.Children.Add(DialogChrome.ButtonRow(_start, reset));
            _image.Visibility = Visibility.Collapsed; RegisterName("NativeLoginImage", _image);
            panelRoot.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Child = _image, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) });
            _status = DialogChrome.Hint("准备就绪。登录成功后还会验证账号身份，通过后才保存和切换。", 12, 10); RegisterName("NativeLoginStatus", _status); panelRoot.Children.Add(_status);
            AddInput(_challenge, "验证码", _code, "NativeLoginCode");
            _submit = DialogChrome.PrimaryButton("提交验证码"); _sendSms = DialogChrome.SecondaryButton("发送短信验证码");
            RegisterName("NativeLoginSubmit", _submit); RegisterName("NativeLoginSendSms", _sendSms);
            _challenge.Children.Add(DialogChrome.ButtonRow(_submit, _sendSms)); _challenge.Visibility = Visibility.Collapsed; panelRoot.Children.Add(_challenge);
            _verify = DialogChrome.SecondaryButton("打开官方验证页面"); RegisterName("NativeLoginVerify", _verify); _verify.Visibility = Visibility.Collapsed; panelRoot.Children.Add(_verify);
            var cancel = DialogChrome.SecondaryButton("取消 / 关闭"); RegisterName("NativeLoginCancel", cancel); panelRoot.Children.Add(DialogChrome.ButtonRow(cancel));
            _start.Click += async (s, e) => await Run(false, "");
            _submit.Click += async (s, e) => await Run(true, _code.Text.Trim());
            _sendSms.Click += async (s, e) => await Run(true, "send-sms");
            reset.Click += (s, e) => Reset(); cancel.Click += (s, e) => Close();
            _verify.Click += (s, e) => { if (_verificationUrl != null) try { Process.Start(new ProcessStartInfo(_verificationUrl) { UseShellExecute = true }); } catch { _status.Text = "无法打开浏览器，请在官方客户端完成设备验证。"; } };
            Closing += (s, e) => { if (_committing) { e.Cancel = true; return; } _closed.Cancel(); _attempt?.Cancel(); _session?.Dispose(); _password.Clear(); _secret.Clear(); };
            Content = new ScrollViewer { Content = panelRoot, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        private void AddInput(Panel panel, string label, Control input, string name)
        {
            panel.Children.Add(DialogChrome.Hint(label, 10, 4));
            if (input is TextBox box) box.Style = TryFindResource("AppTextBox") as Style;
            else input.Style = TryFindResource("AppPasswordBox") as Style;
            System.Windows.Automation.AutomationProperties.SetName(input, label); RegisterName(name, input); panel.Children.Add(input);
        }
        private void Reset()
        {
            if (_committing) return;
            _attempt?.Cancel(); _session?.Dispose(); _session = null; _busy = false;
            _inputs.IsEnabled = _start.IsEnabled = true; _challenge.Visibility = _image.Visibility = _verify.Visibility = Visibility.Collapsed;
            _code.Clear(); _password.Clear(); _status.Text = "已取消上次登录，请重新开始。";
        }
        private async Task Run(bool challenge, string code)
        {
            if (_busy) return;
            _busy = true; _start.IsEnabled = _submit.IsEnabled = _sendSms.IsEnabled = false;
            _inputs.IsEnabled = false; _status.Text = challenge ? "正在提交验证…" : "正在连接官方登录服务…";
            if (_attempt == null || _attempt.IsCancellationRequested) _attempt = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            var attempt = _attempt; var token = attempt.Token;
            try
            {
                if (_qrMode && !challenge) { _session?.Dispose(); _session = null; }
                if (_session == null)
                {
                    var options = NativeCloudAccount.Parse(_options.Serialize()); options.Name = _name.Text.Trim();
                    if (_qrMode && (_kind == PanelKind.Aliyun || _kind == PanelKind.Pan115)) options.ClientId = _appId.Text.Trim();
                    if (_qrMode && _kind == PanelKind.Aliyun) options.ClientSecret = _secret.Password;
                    _session = _factory(_kind, options);
                }
                var session = _session;
                NativeLoginResult result;
                if (challenge) result = await session.SubmitChallengeAsync(code, token);
                else if (!_qrMode) result = await session.PasswordAsync(_username.Text, _password.Password, token);
                else
                {
                    result = await session.StartQrAsync(token); token.ThrowIfCancellationRequested(); ShowResult(result);
                    while (result.State == NativeLoginState.Waiting || result.State == NativeLoginState.Scanned)
                    { await Task.Delay(2000, token); result = await session.PollQrAsync(token); token.ThrowIfCancellationRequested(); ShowResult(result); }
                }
                token.ThrowIfCancellationRequested(); ShowResult(result);
                if (result.State == NativeLoginState.Success)
                {
                    _password.Clear(); _code.Clear(); SecretProtector.ProtectRequired(result.Account.Serialize());
                    _committing = true;
                    if (await _commit(result.Account)) { _committing = false; DialogResult = true; }
                    else { _session.Dispose(); _session = null; _status.Text = "账号身份验证或保存失败。原账号保持不变，请查看主窗口日志后重新登录。"; }
                }
            }
            catch (OperationCanceledException) { if (!_closed.IsCancellationRequested && ReferenceEquals(attempt, _attempt)) _status.Text = "登录已取消或请求超时，请重新开始。"; }
            catch (PanelApiException ex) { if (ReferenceEquals(attempt, _attempt)) _status.Text = ex.Message; }
            catch (Exception) { if (ReferenceEquals(attempt, _attempt)) _status.Text = "登录未完成，请检查应用配置、账号和网络后重新开始。"; }
            finally
            {
                if (ReferenceEquals(attempt, _attempt)) { _committing = false; _busy = false; _submit.IsEnabled = _sendSms.IsEnabled = true; _start.IsEnabled = _inputs.IsEnabled = _challenge.Visibility != Visibility.Visible; }
            }
        }
        private void ShowResult(NativeLoginResult result)
        {
            _status.Text = result.Hint;
            if (!string.IsNullOrEmpty(result.QrText)) { _image.Source = RenderQr(result.QrText); _image.Visibility = Visibility.Visible; }
            if (result.Image != null)
            {
                var image = new BitmapImage(); using (var stream = new MemoryStream(result.Image)) { image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 300; image.StreamSource = stream; image.EndInit(); image.Freeze(); }
                _image.Source = image; _image.Visibility = Visibility.Visible;
            }
            bool code = result.State == NativeLoginState.Captcha || result.State == NativeLoginState.SmsRequired || result.State == NativeLoginState.SmsSent;
            _challenge.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
            _sendSms.Visibility = result.State == NativeLoginState.SmsRequired ? Visibility.Visible : Visibility.Collapsed;
            _submit.Visibility = result.State == NativeLoginState.SmsRequired ? Visibility.Collapsed : Visibility.Visible;
            if (code) _password.Clear();
            _verificationUrl = result.VerificationUrl; _verify.Visibility = result.State == NativeLoginState.Verification ? Visibility.Visible : Visibility.Collapsed;
        }
        private static BitmapSource RenderQr(string text)
        {
            bool[,] matrix = QrEncoder.Encode(text); int n = matrix.GetLength(0), scale = Math.Max(1, 240 / (n + 8)), size = (n + 8) * scale;
            byte[] pixels = new byte[size * size * 4]; for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (matrix[y, x])
                for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++) { int offset = (((y + 4) * scale + dy) * size + (x + 4) * scale + dx) * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0; }
            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); image.Freeze(); return image;
        }
    }
}
