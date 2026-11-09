using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace ZERA.Core.Panels
{
    public sealed partial class NativeLoginSession
    {
        private const string TianAuth = "https://open.e.189.cn";
        private const string TianApp = "9317140619";
        private const string TianReturn = "https://m.cloud.189.cn/zhuanti/2020/loginErrorPc/index.html";
        private string _lt, _reqId, _paramId, _captchaToken, _referer, _username, _encryptedUser, _encryptedPassword, _publicKey, _rsaPrefix, _mobile;
        private NativeLoginState? _challenge;
        private Dictionary<string, string> TianHeaders() => new Dictionary<string, string> { { "REQID", _reqId }, { "lt", _lt }, { "user-finger", _account.UserFinger }, { "Referer", _referer } };
        private static string Match(string text, string pattern) { var match = Regex.Match(text, pattern); return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : ""; }
        private async Task TianyiInit(CancellationToken cancel)
        {
            if (_paramId != null) return;
            if (string.IsNullOrEmpty(_account.UserFinger)) _account.UserFinger = (1000000000L + BitConverter.ToUInt32(Random(4), 0)).ToString();
            if (_account.DeviceId.Length > 0) _cookies.SetCookies(new Uri(TianAuth), "DEVICEID=" + Uri.EscapeDataString(_account.DeviceId) + "; Path=/");
            string url = "https://cloud.189.cn/api/portal/unifyLoginForPC.action?" + Query("appId", TianApp, "clientType", "10020", "returnURL", TianReturn, "timeStamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Reply response = null;
            for (int i = 0; i < 8; i++)
            {
                response = await Request(url, null, cancel, allowRedirect: true).ConfigureAwait(false);
                if (response.Location == null) break;
                Require(SafeUrl(response.Location.AbsoluteUri, "cloud.189.cn", "open.e.189.cn"), "天翼登录页跳转超出官方范围，已停止。");
                url = response.Location.AbsoluteUri;
                if (i == 7) throw new PanelApiException("天翼登录页重定向次数过多。");
            }
            var query = HttpUtility.ParseQueryString(new Uri(url).Query);
            _lt = query["lt"] ?? Match(response.Body, "lt\\s*=\\s*[\"']([^\"']+)");
            _reqId = query["reqId"] ?? Match(response.Body, "reqId\\s*=\\s*[\"']([^\"']+)");
            Require(_lt.Length > 0 && _reqId.Length > 0, "天翼登录页缺少会话参数，请稍后重试。");
            _referer = url; _captchaToken = Match(response.Body, "captchaToken[\"']?\\s+(?:value=[\"'])([^\"']+)");
            var conf = (await Request(TianAuth + "/api/logbox/oauth2/appConf.do", Map("version", "2.0", "appKey", TianApp), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
            _paramId = Text(conf, "data", "paramId"); Require(_paramId.Length > 0, "天翼登录配置无效，请重新开始。");
        }
        private async Task<NativeLoginResult> TianyiQr(CancellationToken cancel)
        {
            await TianyiInit(cancel).ConfigureAwait(false);
            var result = (await Request(TianAuth + "/api/logbox/oauth2/getUUID.do", Map("appId", TianApp), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
            _sid = Text(result, "uuid"); _sign = Text(result, "encryuuid");
            Require(_sid.Length > 0 && _sign.Length > 0, "天翼未返回完整二维码，请重新获取。");
            return new NativeLoginResult { State = NativeLoginState.Waiting, QrText = _sid, Hint = "请使用天翼云盘 App 扫码并在手机确认。" };
        }
        private async Task<NativeLoginResult> TianyiPoll(CancellationToken cancel)
        {
            var now = DateTimeOffset.UtcNow;
            var response = (await Request(TianAuth + "/api/logbox/oauth2/qrcodeLoginState.do", Map("appId", TianApp, "clientType", "1", "returnUrl", TianReturn,
                "paramId", _paramId, "uuid", _sid, "encryuuid", _sign, "cb_SaveName", "3", "isOauth2", "false", "state", "", "date", now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-ddHH:mm:ss.fff"), "timeStamp", now.ToUnixTimeMilliseconds()), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
            string status = Text(response, "status");
            if (status == "-106" || status == "-11002") return Waiting(status == "-11002");
            if (status == "-11001") return new NativeLoginResult { State = NativeLoginState.Expired, Hint = "天翼二维码已过期，请重新获取。" };
            Require(status == "0" && Text(response, "redirectUrl").Length > 0, "天翼扫码未成功或已取消，请重新开始。");
            return await TianyiExchange(Text(response, "redirectUrl"), cancel).ConfigureAwait(false);
        }
        private async Task<NativeLoginResult> TianyiExchange(string redirect, CancellationToken cancel)
        {
            Require(SafeUrl(redirect, "cloud.189.cn", "open.e.189.cn", "api.cloud.189.cn", "m.cloud.189.cn"), "天翼授权返回的跳转地址不在官方范围内。");
            var response = (await Request("https://api.cloud.189.cn/getSessionForPC.action?" + Query("clientType", "TELEPC", "version", "7.2.4.0", "channelId", "web_cloud.189.cn", "returnType", "JSON", "redirectURL", redirect), Map(), cancel).ConfigureAwait(false)).Json;
            _account.SessionKey = Text(response, "sessionKey"); _account.SessionSecret = Text(response, "sessionSecret");
            _account.AccessToken = Text(response, "accessToken"); _account.RefreshToken = Text(response, "refreshToken");
            _account.DeviceId = _cookies.GetCookies(new Uri(TianAuth))["DEVICEID"]?.Value ?? _account.DeviceId;
            Require(_account.SessionKey.Length > 0 && _account.SessionSecret.Length > 0, "天翼授权未返回有效 PC 会话，请重新登录。");
            return Success();
        }
        private string EncryptTian(string value) => _rsaPrefix + NativeLoginCrypto.Rsa(_publicKey, value, false);
        private async Task<NativeLoginResult> TianyiPassword(string password, CancellationToken cancel)
        {
            await TianyiInit(cancel).ConfigureAwait(false);
            var encryption = (await Request(TianAuth + "/api/logbox/config/encryptConf.do", Map("appId", TianApp), cancel, true).ConfigureAwait(false)).Json;
            _publicKey = Text(encryption, "data", "pubKey"); _rsaPrefix = Text(encryption, "data", "pre");
            Require(_publicKey.Length > 0, "天翼未返回密码加密公钥，已停止登录。");
            _encryptedUser = EncryptTian(_username); _encryptedPassword = EncryptTian(password);
            var response = await Request(TianAuth + "/api/logbox/oauth2/needcaptcha.do", Map("appKey", TianApp, "accountType", "02", "userName", _encryptedUser), cancel, true, TianHeaders()).ConfigureAwait(false);
            if (response.Body.Trim() != "0")
            {
                var image = await Request(TianAuth + "/api/logbox/oauth2/picCaptcha.do?" + Query("token", _captchaToken, "REQID", _reqId, "rnd", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), null, cancel, headers: TianHeaders()).ConfigureAwait(false);
                _challenge = NativeLoginState.Captcha;
                return new NativeLoginResult { State = NativeLoginState.Captcha, Image = image.Bytes, Hint = "请输入图片验证码后提交。" };
            }
            return await TianyiSubmit("", cancel).ConfigureAwait(false);
        }
        private async Task<NativeLoginResult> TianyiSubmit(string captcha, CancellationToken cancel)
        {
            var response = (await Request(TianAuth + "/api/logbox/oauth2/loginSubmit.do", Map("version", "v2.0", "apToken", "", "appKey", TianApp, "pageKey", "normal", "accountType", "02",
                "userName", _encryptedUser, "password", _encryptedPassword, "epd", _encryptedPassword, "validateCode", captcha, "captchaToken", _captchaToken, "returnUrl", TianReturn,
                "dynamicCheck", "FALSE", "clientType", "10020", "cb_SaveName", "1", "isOauth2", "false", "state", "", "paramId", _paramId), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
            if (Text(response, "result") == "-133")
            {
                _mobile = Text(response, "mobile"); Require(_mobile.Length > 0, "天翼要求设备验证但未返回验证参数，请改用扫码登录。");
                _challenge = NativeLoginState.SmsRequired;
                return new NativeLoginResult { State = NativeLoginState.SmsRequired, Hint = "本次设备需短信验证，请点击发送验证码。" };
            }
            Require(Text(response, "result") == "0" && Text(response, "toUrl").Length > 0, "天翼账号密码或验证码未通过，请重新登录或使用扫码。");
            return await TianyiExchange(Text(response, "toUrl"), cancel).ConfigureAwait(false);
        }
        private async Task<NativeLoginResult> TianyiChallenge(string code, CancellationToken cancel)
        {
            Require(_challenge.HasValue, "当前没有待完成的验证，请重新登录。");
            if (_challenge == NativeLoginState.Captcha) return await TianyiSubmit(code, cancel).ConfigureAwait(false);
            if (code == "send-sms")
            {
                Require(_challenge == NativeLoginState.SmsRequired, "验证码已经发送，请勿重复请求。");
                var sent = (await Request(TianAuth + "/api/logbox/oauth2/sendSmsCodeForSecondAuth.do", Map("mobile", _mobile, "appKey", TianApp), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
                Require(Text(sent, "result") == "0", "天翼短信发送失败，请稍后重试。"); _challenge = NativeLoginState.SmsSent;
                return new NativeLoginResult { State = NativeLoginState.SmsSent, Hint = "短信已发送，请输入收到的验证码。" };
            }
            Require(_challenge == NativeLoginState.SmsSent, "请先发送短信验证码。");
            var response = (await Request(TianAuth + "/api/logbox/oauth2/submitForSecondAuth.do", Map("mobile", _mobile, "appKey", TianApp, "userName", _encryptedUser,
                "epd", EncryptTian(code), "accountType", "02", "returnUrl", TianReturn, "isOauth2", "false", "cb_SaveName", "1", "state", "", "paramId", _paramId), cancel, true, TianHeaders()).ConfigureAwait(false)).Json;
            Require(Text(response, "result") == "0" && Text(response, "toUrl").Length > 0, "天翼短信验证码未通过，请核对后重试。");
            return await TianyiExchange(Text(response, "toUrl"), cancel).ConfigureAwait(false);
        }
    }
}
