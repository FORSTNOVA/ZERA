using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZERA.Core.Panels
{
    public sealed partial class NativeLoginSession
    {
        private string _creditKey = "";
        private async Task<NativeLoginResult> ThunderPassword(string password, CancellationToken cancel)
        {
            // Public Android SDK protocol identifiers; no user's password is saved in the account document.
            _account.ClientId = "Xp6vsxz_7IYVw2BB";
            _account.ClientSecret = "Xp6vsy4tN9toTVdMSpomVdXpRmES";
            _account.LoginMode = "android";
            _account.UserAgent = "ANDROID-com.xunlei.downloadprovider/8.31.0.9726 netWorkType/WIFI appid/40 deviceName/Xiaomi_M2004j7ac deviceModel/M2004J7AC OSVersion/12 protocolVersion/301 platformVersion/10 sdkVersion/512000";
            _account.DownloadUserAgent = "Dalvik/2.1.0 (Linux; U; Android 12; M2004J7AC Build/SP1A.210812.016)";
            if (_account.DeviceId.Length != 32) _account.DeviceId = Hex(Random(16));
            string package = "com.xunlei.downloadprovider";
            string deviceSign = "div101." + _account.DeviceId + Md5(Sha1(_account.DeviceId + package + "40" + "34a062aaa22f906fca4fefe9fb3a3021"));
            var headers = new Dictionary<string, string> { { "User-Agent", "android-ok-http-client/xl-acc-sdk/version-5.0.12.512000" }, { "x-device-id", _account.DeviceId }, { "x-client-id", _account.ClientId }, { "x-client-version", "8.31.0.9726" } };
            var core = (await Request("https://xluser-ssl.xunlei.com/xluser.core.login/v3/login", Map("protocolVersion", "301", "sequenceNo", "1000012", "platformVersion", "10", "isCompressed", "0", "appid", "40", "clientVersion", "8.31.0.9726", "peerID", "00000000000000000000000000000000", "appName", "ANDROID-" + package, "sdkVersion", "512000", "devicesign", deviceSign, "netWorkType", "WIFI", "providerName", "NONE", "deviceModel", "M2004J7AC", "deviceName", "Xiaomi_M2004j7ac", "OSVersion", "12", "creditkey", _creditKey, "hl", "zh-CN", "userName", _username, "passWord", password, "verifyKey", "", "verifyCode", "", "isMd5Pwd", "0"), cancel, headers: headers).ConfigureAwait(false)).Json;
            if (Text(core, "creditkey").Length > 0) _creditKey = Text(core, "creditkey");
            if (Text(core, "error") == "review_panel")
            {
                string review = Text(core, "reviewurl");
                return ThunderVerification(review + (review.Contains("?") ? "&" : "?") + "deviceid=" + Uri.EscapeDataString(deviceSign));
            }
            string session = Text(core, "sessionID"); Require(session.Length > 0, "迅雷密码登录未通过，请核对账号、密码及设备验证状态。");
            headers["User-Agent"] = _account.UserAgent;
            if (_account.CaptchaToken.Length == 0)
            {
                string field = _username.Contains("@") ? "email" : _username.Length >= 11 && _username.Length <= 18 ? "phone_number" : "username";
                var captcha = (await Request("https://xluser-ssl.xunlei.com/v1/shield/captcha/init", Map("action", "POST:/v1/auth/signin/token", "captcha_token", "", "client_id", _account.ClientId, "device_id", _account.DeviceId, "meta", Map(field, _username), "redirect_uri", "xlaccsdk01://xunlei.com/callback?state=harbor"), cancel, headers: headers).ConfigureAwait(false)).Json;
                _account.CaptchaToken = Text(captcha, "captcha_token");
                if (Text(captcha, "url").Length > 0) return ThunderVerification(Text(captcha, "url"));
                Require(_account.CaptchaToken.Length > 0, "迅雷要求验证，但未返回可用验证码会话，请在官方页面处理后重试。");
            }
            headers["X-Captcha-Token"] = _account.CaptchaToken;
            var token = (await Request("https://xluser-ssl.xunlei.com/v1/auth/signin/token", Map("client_id", _account.ClientId, "client_secret", _account.ClientSecret, "provider", "access_end_point_token", "signin_token", session), cancel, headers: headers).ConfigureAwait(false)).Json;
            _account.AccessToken = Text(token, "access_token"); _account.RefreshToken = Text(token, "refresh_token");
            Require(_account.AccessToken.Length > 0 && _account.RefreshToken.Length > 0, "迅雷令牌兑换未通过，请完成官方验证后重试。");
            return Success();
        }
        private static NativeLoginResult ThunderVerification(string url)
        {
            Require(SafeUrl(url, "xluser-ssl.xunlei.com", "aq.xunlei.com", "i.xunlei.com", "verify.xunlei.com"), "迅雷要求额外验证，但未返回可信的官方验证地址，请在官方客户端登录后导入凭据。");
            return new NativeLoginResult { State = NativeLoginState.Verification, VerificationUrl = url, Hint = "迅雷要求设备/交互验证。打开官方验证页面，完成后回到这里点击密码登录重试。" };
        }
    }
}
