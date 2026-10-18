using System;
using System.Threading;
using System.Windows;
using ZERA.Core.Api;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA
{
    public partial class MainWindow
    {
        private async void OnQueryBaiduMembership(object sender, RoutedEventArgs e)
        {
            if (_panel != PanelKind.Baidu)
            { AppDialog.Show(this, "请先切换到百度网盘平台，再查询当前账号的会员权益。", "百度会员查询"); return; }
            EnsureClient();
            var client = _client;
            var adapter = client == null ? null : PanelAdapter.For(client);
            if (client == null || !client.IsLoggedIn)
            { AppDialog.Show(this, "请先登录百度网盘账号。", "百度会员查询"); return; }
            BaiduMembershipButton.IsEnabled = false;
            try
            {
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                {
                    var info = await adapter.QueryMembershipAsync(cancel.Token);
                    // An account/platform change during the request must not show the previous account's result.
                    if (_client != client || _panel != PanelKind.Baidu) return;
                    string text = "会员身份：" + info.Kind + "\n有效期：" +
                        (info.ExpiresUtc.HasValue ? info.ExpiresUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "接口未返回明确有效期") +
                        "\n查询时间：" + info.QueriedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") +
                        "\n\n剩余极速流量：" + info.TrafficNote +
                        "\n极速流量使用状态：无法确认；会员身份和下载线程数均不能作为已触发的依据。" +
                        "\n最近原生取链限速参数：" + (adapter.ServerSpeedLimitBps > 0 ? Util.FormatSize(adapter.ServerSpeedLimitBps) + "/s（该响应的返回值）" : "未提供 / 未取链，不代表不限速。") +
                        "\n\n本查询只读取会员资料，不启动下载。可到百度官方客户端核对极速流量余额。";
                    AppDialog.Show(this, text, "百度会员 / 极速流量查询", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (_client == client && _panel == PanelKind.Baidu)
                    AppDialog.Show(this, ex is OperationCanceledException ? "查询超时，请稍后重试。" : "会员查询失败，请检查登录和网络后重试。已有身份记录不代表本次查询成功。",
                        "百度会员查询", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { BaiduMembershipButton.IsEnabled = true; }
        }
    }
}
