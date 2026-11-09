using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ZERA.Core.Api
{
    // Modern .NET uses the platform TLS stack directly. Do not ship or attempt
    // to execute the Windows Python runtime inside an Android application.
    public static class QuarkPythonBridge
    {
        public struct ExchangeOutcome { public bool Ok; public string Cookie, Nickname, Error, Detail, PythonPath; }
        public struct AccountInfo { public bool Ok; public string Nickname, Avatar, Error; }
        public static string FindUsablePython(out string diag) { diag = "使用平台 TLS"; return null; }
        public static string GetBundledPythonPath() => null;
        public static string EnsureHelperScript() => throw new PlatformNotSupportedException("此平台使用原生 TLS，不需要 Python。");
        public static AccountInfo? FetchAccountInfo(string cookie)
        {
            try {
                using (var http = new HttpClient { Timeout=TimeSpan.FromSeconds(25) }) {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",cookie);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Mozilla/5.0");
                    using(var document=JsonDocument.Parse(http.GetStringAsync("https://pan.quark.cn/account/info").GetAwaiter().GetResult())) {
                        var root=document.RootElement;
                        if(!root.TryGetProperty("data",out var data))return null;
                        return new AccountInfo {Ok=true,Nickname=data.TryGetProperty("nickname",out var n)?n.ToString():"",Avatar=data.TryGetProperty("avatarUri",out var a)?a.ToString():""};
                    }
                }
            } catch { return null; }
        }
        public static ExchangeOutcome? Exchange(string ticket)
        {
            var jar=new CookieContainer();
            try {
                using(var handler=new HttpClientHandler {CookieContainer=jar,UseCookies=true,AllowAutoRedirect=true})
                using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(30)}) {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","Mozilla/5.0");
                    using(var response=http.GetAsync("https://pan.quark.cn/account/info?st="+Uri.EscapeDataString(ticket)+"&lw=scan").GetAwaiter().GetResult()) {
                        response.EnsureSuccessStatusCode();
                        string cookie=jar.GetCookieHeader(new Uri("https://pan.quark.cn/"));
                        bool ok=cookie.Contains("__pus")||cookie.Contains("__puus");
                        return new ExchangeOutcome {Ok=ok,Cookie=cookie,Error=ok?"":"平台未返回有效登录 Cookie，请改用 Cookie 导入。"};
                    }
                }
            } catch {return new ExchangeOutcome {Ok=false,Error="平台 TLS 换取登录态失败，请重试或导入 Cookie。"};}
        }
    }
}
