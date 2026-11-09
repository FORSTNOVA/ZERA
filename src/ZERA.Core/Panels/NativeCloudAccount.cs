using System;
using System.Linq;
using System.Web.Script.Serialization;

namespace ZERA.Core.Panels
{
    // Stored in the legacy credential slot, encrypted by the account/settings stores.
    public sealed class NativeCloudAccount
    {
        public string Name { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string SessionKey { get; set; } = "";
        public string SessionSecret { get; set; } = "";
        public string Authorization { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string CaptchaToken { get; set; } = "";
        public string UserAgent { get; set; } = "Mozilla/5.0";
        public string DownloadUserAgent { get; set; } = "";
        public string Identity { get; set; } = "";
        public string DriveId { get; set; } = "";
        public string LoginMode { get; set; } = "";
        public string UserFinger { get; set; } = "";
        public string Serialize() => new JavaScriptSerializer().Serialize(this);
        public static bool Supports(PanelKind kind) => kind >= PanelKind.Aliyun && kind <= PanelKind.Thunder;
        public static NativeCloudAccount Parse(string credential)
        {
            try
            {
                var value = new JavaScriptSerializer().Deserialize<NativeCloudAccount>(credential);
                if (value == null || new[] { value.AccessToken, value.RefreshToken, value.ClientId, value.ClientSecret,
                    value.SessionKey, value.SessionSecret, value.Authorization, value.DeviceId, value.CaptchaToken, value.UserAgent, value.DownloadUserAgent, value.UserFinger, value.LoginMode, value.Identity, value.DriveId, value.Name }
                    .Any(s => s == null || s.Any(char.IsControl))) throw new ArgumentException();
                return value;
            }
            catch { throw new ArgumentException("网盘授权配置无效，请重新填写凭据。"); }
        }
        public void Validate(PanelKind kind)
        {
            bool valid;
            switch (kind)
            {
                case PanelKind.Aliyun: valid = AccessToken.Length > 0 || RefreshToken.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0; break;
                case PanelKind.Pan123: valid = AccessToken.Length > 0 || ClientId.Length > 0 && ClientSecret.Length > 0; break;
                case PanelKind.Pan115: valid = AccessToken.Length > 0 || RefreshToken.Length > 0; break;
                case PanelKind.Tianyi: valid = AccessToken.Length > 0 || SessionKey.Length > 0 && SessionSecret.Length > 0; break;
                case PanelKind.Mobile139: valid = Authorization.Length > 0; break;
                case PanelKind.Thunder: valid = AccessToken.Length > 0 || RefreshToken.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0; break;
                default: valid = false; break;
            }
            if (!valid) throw new ArgumentException("请按该网盘的说明填写有效令牌或应用授权信息。");
        }
        public static string Guide(PanelKind kind)
        {
            switch (kind)
            {
                case PanelKind.Aliyun: return "支持开放平台扫码登录，需获授权应用的 Client ID 与 Client Secret；也可导入开放平台令牌。暂不提供密码登录，普通网页令牌不能代替开放平台令牌。";
                case PanelKind.Pan123: return "支持账号密码登录（网站接口），也可使用开放平台应用信息或令牌导入（开放平台接口）。两种令牌分别路由，暂不支持扫码。下载仍取决于账号权限与平台限制。";
                case PanelKind.Pan115: return "支持开放平台扫码登录，需应用 App ID；也可导入开放平台 Access Token / Refresh Token。暂不提供密码登录，普通网页 Cookie 不能代替开放平台令牌。";
                case PanelKind.Tianyi: return "支持天翼 App 扫码或账号密码登录，按平台要求完成图片 / 短信验证；也可导入 PC 会话。仅个人云。";
                case PanelKind.Mobile139: return "支持 139 邮箱 / 手机号服务密码登录及短信设备验证，也可导入网页版 Authorization。仅新版个人云；暂不支持扫码。";
                case PanelKind.Thunder: return "支持账号密码登录，设备 / 交互验证需在官方页面完成后重试；也可导入当前会话令牌。暂不支持扫码，服务端验证码和账号权益可能限制接口调用。";
                default: return "";
            }
        }
    }
}
