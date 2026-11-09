using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace ZERA.Core.Panels
{
    public sealed partial class NativeLoginSession
    {
        // Public wire-protocol constants used by the mobile cloud / mail SSO endpoints.
        private const string MailPublicKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAnsOHTFtwW5rq/8gGhPlM5Z3RPdeN/d+FYIHb5JmcfBOCozXuT8c+0anvxtkjzghixwNlnmBuhN8OYfS789YuH/qReQbHC7OdlisLildNWPHRNUYcPa0W3lXSG3+81CXK7FDXPvXo5ubw2GqVbIsccMarI1dyfXdi4ITiCXvmM9wYBdUs9yXtoorhlpyYUI2GV8HNsQjWK9P5QZHT3ox5Qy+mjRmvv6RUFJLPOMkOS/pGZ0DwC1ypFZBxstW0/ftVupdOmGWvW7J2/e3dq3A/UvIkC4YUY/diL1wighJx1G9MiRROISjNMvNyUDSTqPJy516+l3sgHEbc067QIJx2NQIDAQAB";
        private string _mailRisk;
        private Dictionary<string, string> MailHeaders() => new Dictionary<string, string> { { "User-Agent", "okhttp/4.12.0" }, { "Referer", "https://mail.10086.cn/" }, { "Origin", "https://mail.10086.cn" } };
        private string MailSid(Reply response)
        {
            string sid = response.Location == null ? "" : HttpUtility.ParseQueryString(response.Location.Query)["sid"] ?? "";
            if (sid.Length == 0) sid = _cookies.GetCookies(new Uri("https://mail.10086.cn/"))["Os_SSo_Sid"]?.Value ?? _cookies.GetCookies(new Uri("https://mail.10086.cn/"))["sid"]?.Value ?? "";
            return sid;
        }
        private async Task<NativeLoginResult> MobilePassword(string password, CancellationToken cancel)
        {
            // A failed prior SSO attempt must not let old mail authentication masquerade as a new password login.
            foreach (Cookie cookie in _cookies.GetCookies(new Uri("https://mail.10086.cn/")))
                if (cookie.Name == "RMKEY" || cookie.Name == "Os_SSo_Sid" || cookie.Name == "sid") cookie.Expired = true;
            var response = await Request("https://mail.10086.cn/Login/Login.ashx", Map("UserName", _username, "passOld", "", "auto", "on", "Password", Sha1("fetion.com.cn:" + password), "webIndexPagePwdLogin", "1", "pwdType", "1", "clientId", "1003", "authType", "2"), cancel, true, MailHeaders(), true).ConfigureAwait(false);
            _mailRisk = response.Location == null ? "" : HttpUtility.ParseQueryString(response.Location.Query)["ec"] ?? "";
            if (_mailRisk.Length > 0)
            {
                Require(new[] { "PML401010062", "MW0016", "S025", "S035" }.Contains(_mailRisk), "移动邮箱要求额外验证，请先在官方网页完成验证后重新登录。");
                _challenge = NativeLoginState.SmsRequired;
                return new NativeLoginResult { State = NativeLoginState.SmsRequired, Hint = "移动邮箱需短信验证，请点击发送验证码。" };
            }
            string sid = MailSid(response); Require(sid.Length > 0, "移动账号密码未通过，或需要在官方网页完成验证。请核对 139 邮箱/手机号账号与服务密码。");
            return await MobileExchange(sid, cancel).ConfigureAwait(false);
        }
        private static string Xml(params string[] fields)
        {
            var text = new StringBuilder("<object>");
            for (int i = 0; i < fields.Length; i += 2) text.Append("<string name=\"").Append(SecurityElement.Escape(fields[i])).Append("\">").Append(SecurityElement.Escape(fields[i + 1])).Append("</string>");
            return text.Append("</object>").ToString();
        }
        private async Task<NativeLoginResult> MobileChallenge(string code, CancellationToken cancel)
        {
            Require(_challenge == NativeLoginState.SmsRequired || _challenge == NativeLoginState.SmsSent, "当前没有待完成的移动短信验证。");
            string encryptedName = NativeLoginCrypto.Rsa(MailPublicKey, _username, true);
            string url = "https://mail.10086.cn/s?";
            if (code == "send-sms")
            {
                Require(_challenge == NativeLoginState.SmsRequired, "验证码已经发送，请勿重复请求。");
                string scene = _mailRisk == "PML401010062" ? "2" : _mailRisk == "MW0016" ? "4" : "1";
                var response = (await Request(url + Query("func", "login:sendSmsCodeByScene", "cguid", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Xml("loginName", encryptedName, "fv", "4", "clientId", "1003", "eMode", "1", "loginFailureUrl", "", "loginSuccessUrl", "", "verifyCode", "", "version", "1.0", "scene", scene), cancel, headers: MailHeaders()).ConfigureAwait(false)).Json;
                Require(Text(response, "code") == "S_OK", "移动短信发送未通过，可能要求图片验证或请求过于频繁。请稍后在官方页面处理。");
                _challenge = NativeLoginState.SmsSent; return new NativeLoginResult { State = NativeLoginState.SmsSent, Hint = "短信已发送，请输入收到的验证码。" };
            }
            Require(_challenge == NativeLoginState.SmsSent, "请先发送验证码。");
            string body = Xml("clientId", "1003", "version", "4", "loginType", "0", "authType", "2", "loginName", encryptedName, "eMode", "1", "loginPassword", Sha1("fetion.com.cn:" + code), "createAutoLoginSecretKey", "1", "verifyCode", "", "verifyAgentId", "", "reqFrom", "3", "needWCookie", "1");
            if (_mailRisk == "MW0016") body = body.Replace("</object>", "<string name=\"pwdType\">1</string></object>");
            var reply = await Request(url + Query("func", "/login/inlogin.action", "cguid", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), body, cancel, headers: MailHeaders()).ConfigureAwait(false);
            var verified = reply.Json; Require(Text(verified, "code") == "S_OK", "移动短信验证码未通过，请核对后重试。");
            string sid = MailSid(reply); string successUrl = Text(verified, "var", "loginSuccessUrl");
            if (sid.Length == 0 && Uri.TryCreate(successUrl, UriKind.Absolute, out var success)) sid = HttpUtility.ParseQueryString(success.Query)["sid"] ?? "";
            Require(sid.Length > 0, "移动验证通过但缺少会话，请重新登录。"); return await MobileExchange(sid, cancel).ConfigureAwait(false);
        }
        private static byte[] Unhex(string value)
        { if (value.Length % 2 != 0) throw new FormatException(); return Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray(); }
        private static byte[] Aes(byte[] bytes, byte[] key, byte[] iv, bool encrypt)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key; aes.Mode = iv == null ? CipherMode.ECB : CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                if (iv != null) aes.IV = iv;
                using (var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor()) return transform.TransformFinalBlock(bytes, 0, bytes.Length);
            }
        }
        private async Task<NativeLoginResult> MobileExchange(string sid, CancellationToken cancel)
        {
            string rmkey = _cookies.GetCookies(new Uri("https://mail.10086.cn/"))["RMKEY"]?.Value ?? "";
            Require(rmkey.Length > 0 && !rmkey.Any(char.IsControl), "移动登录未返回 RMKEY 会话，请重新登录。");
            var headers = MailHeaders(); headers["Cookie"] = "RMKEY=" + rmkey;
            var artifact = (await Request("https://smsrebuild1.mail.10086.cn/setting/s?" + Query("func", "umc:getArtifact", "sid", sid, "cguid", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "", cancel, headers: headers).ConfigureAwait(false)).Json;
            string ticket = Text(artifact, "var", "artifact"); Require(ticket.Length > 0, "移动邮箱未返回云盘授权票据，请重新登录。");
            var body = new SortedDictionary<string, object>(StringComparer.Ordinal) {
                { "clientkey_decrypt", "l3TryM&Q+X7@dzwk)qP" }, { "clienttype", "886" }, { "cpid", "507" }, { "dycpwd", ticket },
                { "extInfo", Map("ifOpenAccount", "0") }, { "loginMode", "0" }, { "msisdn", _username }, { "pintype", "13" },
                { "secinfo", Sha1("fetion.com.cn:" + ticket).ToUpperInvariant() }, { "version", "20250901" } };
            byte[] key = Unhex("73634235495062495331515373756c734e7253306c673d3d"), iv = Random(16);
            byte[] cipher = Aes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body)), key, iv, true);
            string payload = Convert.ToBase64String(iv.Concat(cipher).ToArray());
            var ssoHeaders = new Dictionary<string, string> { { "hcy-cool-flag", "1" }, { "x-huawei-channelSrc", "10246600" }, { "x-MM-Source", "0" }, { "x-UserAgent", "android|23116PN5BC|android15|1.2.6|||1440x3200|10246600" }, { "x-DeviceInfo", "4|127.0.0.1|5|1.2.6|Xiaomi|23116PN5BC||02-00-00-00-00-00|android 15|1440x3200|android|||" }, { "User-Agent", "okhttp/3.12.2" } };
            var reply = await Request("https://user-njs.yun.139.com/user/thirdlogin", payload, cancel, headers: ssoHeaders).ConfigureAwait(false);
            object result;
            try
            {
                string plain = reply.Body.Trim();
                if (!plain.StartsWith("{", StringComparison.Ordinal)) { byte[] decoded = Convert.FromBase64String(plain); Require(decoded.Length >= 32, "移动云盘授权响应无效。"); plain = Encoding.UTF8.GetString(Aes(decoded.Skip(16).ToArray(), key, decoded.Take(16).ToArray(), false)); }
                var first = new JavaScriptSerializer().DeserializeObject(plain);
                string inner = Text(first, "data"); Require(inner.Length > 0, "移动云盘未返回授权数据。");
                result = new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(Aes(Unhex(inner), Unhex("7150714477323633586746674c337538"), null, false)));
            }
            catch (PanelApiException) { throw; }
            catch { throw new PanelApiException("移动云盘授权数据格式已变化，请改用授权凭据导入并反馈日志。"); }
            string account = Text(result, "account"), token = Text(result, "authToken");
            Require(account.Length > 0 && token.Length > 0, "移动云盘未返回有效授权，请核对服务开通状态。");
            _account.Authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes("pc:" + account + ":" + token)); return Success();
        }
    }
}
